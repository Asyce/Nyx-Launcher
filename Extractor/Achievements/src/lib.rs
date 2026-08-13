pub mod capture;
pub mod cli;
pub mod decoder;
pub mod launcher;
pub mod launcher_app;
pub mod npcap;
pub mod output;
pub mod security;

use std::collections::BTreeSet;
use std::fmt;

include!(concat!(env!("OUT_DIR"), "/catalog_ids.rs"));

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Game {
    Gi,
    Hsr,
}

impl Game {
    pub const fn key(self) -> &'static str {
        match self {
            Self::Gi => "gi",
            Self::Hsr => "hsr",
        }
    }

    pub const fn output_folder(self) -> &'static str {
        match self {
            Self::Gi => "Genshin Impact",
            Self::Hsr => "Honkai Star Rail",
        }
    }

    pub const fn catalog_version(self) -> &'static str {
        match self {
            Self::Gi => "gi-6.7",
            Self::Hsr => "hsr-4.4",
        }
    }

    pub const fn ports(self) -> [u16; 2] {
        match self {
            Self::Gi => [22101, 22102],
            Self::Hsr => [23301, 23302],
        }
    }

    pub fn released_ids(self) -> &'static [u32] {
        match self {
            Self::Gi => GI_IDS,
            Self::Hsr => HSR_IDS,
        }
    }

    pub fn other_ids(self) -> &'static [u32] {
        match self {
            Self::Gi => HSR_IDS,
            Self::Hsr => GI_IDS,
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct AchievementRecord {
    pub id: u32,
    pub status: u32,
}

#[derive(Debug, Eq, PartialEq)]
pub enum SnapshotError {
    Empty,
    Duplicate(u32),
    WrongGame(u32),
    Unknown(u32),
    InvalidStatus(u32),
    NoCompleted,
}

impl fmt::Display for SnapshotError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Empty => write!(f, "the recognized achievement snapshot was empty"),
            Self::Duplicate(_) => write!(f, "the snapshot contained a duplicate achievement"),
            Self::WrongGame(_) => write!(f, "the snapshot belongs to the other game"),
            Self::Unknown(id) => {
                write!(
                    f,
                    "the snapshot contained an unreleased achievement ID {id}"
                )
            }
            Self::InvalidStatus(_) => write!(f, "the snapshot contained an invalid status"),
            Self::NoCompleted => write!(f, "the snapshot contained no completed achievements"),
        }
    }
}

impl std::error::Error for SnapshotError {}

pub fn validate_complete_snapshot(
    game: Game,
    records: &[AchievementRecord],
    selected_catalog: &[u32],
    other_catalog: &[u32],
) -> Result<Vec<u32>, SnapshotError> {
    if records.is_empty() {
        return Err(SnapshotError::Empty);
    }
    let selected: BTreeSet<u32> = selected_catalog.iter().copied().collect();
    let other: BTreeSet<u32> = other_catalog.iter().copied().collect();
    let mut seen = BTreeSet::new();
    let mut completed = Vec::new();
    for record in records {
        let is_selected = selected.contains(&record.id);
        let is_other = other.contains(&record.id);
        // Star Rail's proven source packet is the complete quest list, not an
        // achievement-only list. Match StarDB: filter that list to catalogued
        // achievement IDs instead of rejecting unrelated quests. Genshin's
        // source packet remains achievement-only and keeps strict validation.
        if game == Game::Hsr && !is_selected && !is_other {
            continue;
        }
        if !seen.insert(record.id) {
            return Err(SnapshotError::Duplicate(record.id));
        }
        if record.status > 4 {
            return Err(SnapshotError::InvalidStatus(record.status));
        }
        if is_selected {
            if record.status == 2 || record.status == 3 {
                completed.push(record.id);
            }
        } else if is_other {
            return Err(SnapshotError::WrongGame(record.id));
        } else if record.status >= 2 {
            return Err(SnapshotError::Unknown(record.id));
        }
    }
    if completed.is_empty() {
        return Err(SnapshotError::NoCompleted);
    }
    completed.sort_unstable();
    Ok(completed)
}

#[cfg(test)]
mod tests {
    use super::*;
    use sha2::{Digest, Sha256};

    fn records() -> Vec<AchievementRecord> {
        vec![
            AchievementRecord { id: 10, status: 0 },
            AchievementRecord { id: 20, status: 2 },
            AchievementRecord { id: 30, status: 3 },
        ]
    }

    #[test]
    fn complete_snapshot_filters_only_finished_statuses() {
        assert_eq!(
            validate_complete_snapshot(Game::Gi, &records(), &[10, 20, 30], &[40]).unwrap(),
            vec![20, 30]
        );
    }

    #[test]
    fn snapshot_rejects_wrong_unknown_duplicate_and_empty() {
        assert_eq!(
            validate_complete_snapshot(Game::Gi, &[], &[10], &[40]),
            Err(SnapshotError::Empty)
        );
        let mut duplicate = records();
        duplicate.push(AchievementRecord { id: 20, status: 2 });
        assert_eq!(
            validate_complete_snapshot(Game::Gi, &duplicate, &[10, 20, 30], &[40]),
            Err(SnapshotError::Duplicate(20))
        );
        assert_eq!(
            validate_complete_snapshot(
                Game::Gi,
                &[AchievementRecord { id: 40, status: 2 }],
                &[10],
                &[40]
            ),
            Err(SnapshotError::WrongGame(40))
        );
        assert_eq!(
            validate_complete_snapshot(
                Game::Gi,
                &[AchievementRecord { id: 99, status: 2 }],
                &[10],
                &[40]
            ),
            Err(SnapshotError::Unknown(99))
        );
        assert_eq!(
            SnapshotError::Unknown(99).to_string(),
            "the snapshot contained an unreleased achievement ID 99"
        );
        assert_eq!(
            validate_complete_snapshot(Game::Gi, &records()[..2], &[10, 20, 30], &[40]),
            Ok(vec![20])
        );
    }

    #[test]
    fn unknown_unfinished_rows_are_ignored_but_all_other_validation_stays_strict() {
        for status in [0, 1] {
            assert_eq!(
                validate_complete_snapshot(
                    Game::Gi,
                    &[
                        AchievementRecord { id: 10, status: 2 },
                        AchievementRecord { id: 99, status },
                    ],
                    &[10],
                    &[40],
                ),
                Ok(vec![10])
            );
        }

        for status in [2, 3, 4] {
            assert_eq!(
                validate_complete_snapshot(
                    Game::Gi,
                    &[
                        AchievementRecord { id: 10, status: 2 },
                        AchievementRecord { id: 99, status },
                    ],
                    &[10],
                    &[40],
                ),
                Err(SnapshotError::Unknown(99))
            );
        }

        assert_eq!(
            validate_complete_snapshot(
                Game::Gi,
                &[
                    AchievementRecord { id: 10, status: 2 },
                    AchievementRecord { id: 99, status: 5 },
                ],
                &[10],
                &[40],
            ),
            Err(SnapshotError::InvalidStatus(5))
        );

        assert_eq!(
            validate_complete_snapshot(
                Game::Gi,
                &[
                    AchievementRecord { id: 10, status: 2 },
                    AchievementRecord { id: 99, status: 0 },
                    AchievementRecord { id: 99, status: 1 },
                ],
                &[10],
                &[40],
            ),
            Err(SnapshotError::Duplicate(99))
        );

        for status in 0..=4 {
            assert_eq!(
                validate_complete_snapshot(
                    Game::Gi,
                    &[
                        AchievementRecord { id: 10, status: 2 },
                        AchievementRecord { id: 40, status },
                    ],
                    &[10],
                    &[40],
                ),
                Err(SnapshotError::WrongGame(40))
            );
        }
        assert_eq!(
            validate_complete_snapshot(
                Game::Gi,
                &[
                    AchievementRecord { id: 10, status: 2 },
                    AchievementRecord { id: 40, status: 5 },
                ],
                &[10],
                &[40],
            ),
            Err(SnapshotError::InvalidStatus(5))
        );

        assert_eq!(
            validate_complete_snapshot(
                Game::Gi,
                &[
                    AchievementRecord { id: 10, status: 2 },
                    AchievementRecord { id: 99, status: 0 },
                ],
                &[10, 20],
                &[40],
            ),
            Ok(vec![10])
        );
    }

    #[test]
    fn hsr_quest_snapshot_filters_unrelated_quests_like_stardb() {
        assert_eq!(
            validate_complete_snapshot(
                Game::Hsr,
                &[
                    AchievementRecord { id: 10, status: 2 },
                    AchievementRecord { id: 99, status: 2 },
                    AchievementRecord { id: 99, status: 7 },
                    AchievementRecord { id: 100, status: 3 },
                ],
                &[10, 20],
                &[40],
            ),
            Ok(vec![10])
        );
        assert_eq!(
            validate_complete_snapshot(
                Game::Hsr,
                &[AchievementRecord { id: 99, status: 2 }],
                &[10],
                &[40],
            ),
            Err(SnapshotError::NoCompleted)
        );
    }

    #[test]
    fn embedded_catalog_counts_are_pinned() {
        assert_eq!(GI_IDS.len(), 1759);
        assert_eq!(HSR_IDS.len(), 1869);
        assert!(GI_IDS.windows(2).all(|pair| pair[0] < pair[1]));
        assert!(HSR_IDS.windows(2).all(|pair| pair[0] < pair[1]));
        let database =
            std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../../Database/Achievements");
        let normalize =
            |bytes: Vec<u8>| {
                assert!(!bytes.iter().enumerate().any(|(index, byte)| {
                    *byte == b'\r' && bytes.get(index + 1) != Some(&b'\n')
                }));
                bytes
                    .into_iter()
                    .filter(|byte| *byte != b'\r')
                    .collect::<Vec<_>>()
            };
        let gi = normalize(std::fs::read(database.join("gi/catalog.json")).unwrap());
        let hsr = normalize(std::fs::read(database.join("hsr/catalog.json")).unwrap());
        assert_eq!(
            format!("{:x}", Sha256::digest(&gi)),
            "5608dd41a26a06639c6455d65de7abdd2a7e5e997f55c6ed93dec6d08dc673b5"
        );
        assert_eq!(
            format!("{:x}", Sha256::digest(&hsr)),
            "1686a1deb2a03e758e1047684acc9e760d5c793b2e2717bb4d1bc9eeb7c60502"
        );
        let gi_value: serde_json::Value = serde_json::from_slice(&gi).unwrap();
        let hsr_value: serde_json::Value = serde_json::from_slice(&hsr).unwrap();
        assert_eq!(
            format!("gi-{}", gi_value["catalogVersion"].as_str().unwrap()),
            Game::Gi.catalog_version()
        );
        assert_eq!(
            format!("hsr-{}", hsr_value["catalogVersion"].as_str().unwrap()),
            Game::Hsr.catalog_version()
        );
    }
}
