use crate::capture::{DecoderProgress, SnapshotDecoder};
use crate::{AchievementRecord, Game};
use auto_artifactarium::{
    GamePacket as GiPacket, GameSniffer as GiSniffer, matches_achievement_packet as gi_achievements,
};
use auto_reliquary::{
    GamePacket as HsrPacket, GameSniffer as HsrSniffer,
    matches_achievement_packet as hsr_achievements,
};
use base64::{Engine, prelude::BASE64_STANDARD};
use std::collections::HashMap;

pub const STARDB_SOURCE_COMMIT: &str = "50c04597d37cf366290de6e316aaca98dd57acfc";
pub const GI_SOURCE_SHA256: &str =
    "e0e1fcbfb6aa5d727367a60574b7688a4da14abe12c5a3bdad3a7fc87c694d18";
pub const HSR_SOURCE_SHA256: &str =
    "85a98f5abf9b4041d6752e8f60b6db760d5a9753ad73874a9d5744f9c1d7944a";
pub const GI_CANONICAL_SHA256: &str =
    "37ccd359c35b0f990032e7941ed140914a322b935706a1c66d252b27dd74f3c3";
pub const HSR_CANONICAL_SHA256: &str =
    "8ffac930c0ff2821c0d8f9c0bcbcdaba64a8be0395c6263572c3c5afa65d34ec";

enum GameDecoderInner {
    Gi(GiSniffer),
    Hsr(HsrSniffer),
}

pub struct GameDecoder {
    inner: GameDecoderInner,
    progress: DecoderProgress,
}

impl GameDecoder {
    pub fn new(game: Game) -> Result<Self, String> {
        match game {
            Game::Gi => {
                let encoded: HashMap<u16, String> =
                    serde_json::from_slice(include_bytes!("../keys/gi.json"))
                        .map_err(|_| "invalid embedded GI keys")?;
                let keys = encoded
                    .into_iter()
                    .map(|(id, value)| {
                        BASE64_STANDARD
                            .decode(value)
                            .map(|bytes| (id, bytes))
                            .map_err(|_| "invalid embedded GI key")
                    })
                    .collect::<Result<HashMap<_, _>, _>>()?;
                Ok(Self {
                    inner: GameDecoderInner::Gi(GiSniffer::new().set_initial_keys(keys)),
                    progress: DecoderProgress::None,
                })
            }
            Game::Hsr => {
                let encoded: HashMap<u32, String> =
                    serde_json::from_slice(include_bytes!("../keys/hsr.json"))
                        .map_err(|_| "invalid embedded HSR keys")?;
                let keys = encoded
                    .into_iter()
                    .map(|(id, value)| {
                        BASE64_STANDARD
                            .decode(value)
                            .map(|bytes| (id, bytes))
                            .map_err(|_| "invalid embedded HSR key")
                    })
                    .collect::<Result<HashMap<_, _>, _>>()?;
                Ok(Self {
                    inner: GameDecoderInner::Hsr(HsrSniffer::new().set_initial_keys(keys)),
                    progress: DecoderProgress::None,
                })
            }
        }
    }
}

impl SnapshotDecoder for GameDecoder {
    fn decode(&mut self, frame: &[u8]) -> Option<Vec<AchievementRecord>> {
        match &mut self.inner {
            GameDecoderInner::Gi(sniffer) => {
                let packet = sniffer.receive_packet(frame.to_vec())?;
                let GiPacket::Commands(commands) = packet else {
                    self.progress = self.progress.max(DecoderProgress::Transport);
                    return None;
                };
                self.progress = self.progress.max(if commands.is_empty() {
                    DecoderProgress::Transport
                } else {
                    DecoderProgress::Commands
                });
                commands.into_iter().find_map(|command| {
                    gi_achievements(&command).map(|rows| {
                        rows.into_iter()
                            .map(|row| AchievementRecord {
                                id: row.id,
                                status: row.status,
                            })
                            .collect()
                    })
                })
            }
            GameDecoderInner::Hsr(sniffer) => {
                let packet = sniffer.receive_packet(frame.to_vec())?;
                let HsrPacket::Commands(commands) = packet else {
                    self.progress = self.progress.max(DecoderProgress::Transport);
                    return None;
                };
                self.progress = self.progress.max(if commands.is_empty() {
                    DecoderProgress::Transport
                } else {
                    DecoderProgress::Commands
                });
                commands.into_iter().find_map(|command| {
                    hsr_achievements(&command).map(|rows| {
                        rows.into_iter()
                            .map(|row| AchievementRecord {
                                id: row.id,
                                status: row.status,
                            })
                            .collect()
                    })
                })
            }
        }
    }

    fn progress(&self) -> DecoderProgress {
        self.progress
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use sha2::{Digest, Sha256};

    fn hash(bytes: &[u8]) -> String {
        format!("{:x}", Sha256::digest(bytes))
    }

    #[test]
    fn authorized_key_maps_have_exact_hashes_and_counts() {
        let gi = include_bytes!("../keys/gi.json");
        let hsr = include_bytes!("../keys/hsr.json");
        let gi_map = serde_json::from_slice::<std::collections::BTreeMap<u32, String>>(gi).unwrap();
        let hsr_map =
            serde_json::from_slice::<std::collections::BTreeMap<u32, String>>(hsr).unwrap();
        assert_eq!(gi_map.len(), 10);
        assert_eq!(hsr_map.len(), 30);
        assert_eq!(
            hash(&serde_json::to_vec(&gi_map).unwrap()),
            GI_CANONICAL_SHA256
        );
        assert_eq!(
            hash(&serde_json::to_vec(&hsr_map).unwrap()),
            HSR_CANONICAL_SHA256
        );
        assert_eq!(GI_SOURCE_SHA256.len(), 64);
        assert_eq!(HSR_SOURCE_SHA256.len(), 64);
        assert_eq!(STARDB_SOURCE_COMMIT.len(), 40);
    }

    #[test]
    fn hsr_key_update_preserves_all_previous_entries() {
        let mut hsr_map = serde_json::from_slice::<std::collections::BTreeMap<u32, String>>(
            include_bytes!("../keys/hsr.json"),
        )
        .unwrap();
        let added = hsr_map
            .remove(&3136867684)
            .expect("new pinned HSR key is missing");
        let decoded = BASE64_STANDARD.decode(&added).unwrap();
        assert_eq!(decoded.len(), 4096);
        assert!(
            BASE64_STANDARD.encode(&decoded) == added,
            "new HSR key must use canonical standard base64"
        );
        assert_eq!(hsr_map.len(), 29);
        assert_eq!(
            hash(&serde_json::to_vec(&hsr_map).unwrap()),
            "e9381b6b79fd2a41dd3c7ade82508c5eafec9f19e15d7fa2bc0e4a7bcdd42512"
        );
    }

    #[test]
    fn embedded_parser_rsa_keys_match_the_pinned_upstream_source() {
        fn lf(bytes: &[u8]) -> Vec<u8> {
            String::from_utf8(bytes.to_vec())
                .unwrap()
                .replace("\r\n", "\n")
                .into_bytes()
        }
        let key4 = include_bytes!("../vendor/auto-artifactarium/keys/private_key_4.pem");
        let key5 = include_bytes!("../vendor/auto-artifactarium/keys/private_key_5.pem");
        assert_eq!(
            hash(&lf(key4)),
            "c43fafade9dbc63440339fab24fa19d5ae78bc69e60d66ee956d951d6ff6392f"
        );
        assert_eq!(
            hash(&lf(key5)),
            "6a3fbd53387f9d13230f8558e40df18ad3a8fc11fc23da83a202eedc3bd70ce3"
        );
        fn der(bytes: &[u8]) -> Vec<u8> {
            let encoded = String::from_utf8(bytes.to_vec())
                .unwrap()
                .lines()
                .filter(|line| !line.starts_with("-----"))
                .collect::<String>();
            BASE64_STANDARD.decode(encoded).unwrap()
        }
        assert_eq!(
            hash(&der(key4)),
            "e27f729e1944a7550b51d27b3c3bf4b680209cb982413d3245d56df2ae7f0602"
        );
        assert_eq!(
            hash(&der(key5)),
            "b4ab7873b89540628de48a250747d0746f3c76e64a17b77dad221578a60fd996"
        );
    }
}
