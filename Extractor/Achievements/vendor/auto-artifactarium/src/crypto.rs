use std::collections::HashMap;

use crate::cs_rand::Random;
use rand_mt::Mt64;
use tracing::{debug, info, instrument, warn};
use zeroize::Zeroizing;

#[instrument(skip_all)]
pub fn decrypt_command(key: &[u8], encrypted: &mut [u8]) {
    for i in 0..encrypted.len() {
        encrypted[i] ^= key[i % key.len()];
    }
}

pub fn lookup_initial_key(
    initial_keys: &HashMap<u16, Zeroizing<Vec<u8>>>,
    bytes: &[u8],
) -> Option<Zeroizing<Vec<u8>>> {
    let version = u16::from_be_bytes(bytes.get(..2)?.try_into().ok()?) ^ 0x4567;

    // attempt to fetch from user provided initial keys, otherwise use our own baked-in ones
    let key = initial_keys
        .get(&version)
        .filter(|key| !key.is_empty())
        .cloned();
    match key {
        Some(key) => {
            info!(version, "found initial decryption key");
            Some(key)
        }
        None => {
            info!(version, "didn't find decryption key");
            None
        }
    }
}

pub fn new_key_from_seed(seed: u64) -> Zeroizing<Vec<u8>> {
    // mersenne twister generator
    let mut first = Mt64::new(seed);
    let mut gen = Mt64::new(first.next_u64());

    let _ = gen.next_u64(); // skip first number

    let mut key = Zeroizing::new(vec![0; 4096]);
    for chunk in key.chunks_exact_mut(8) {
        chunk.copy_from_slice(&gen.next_u64().to_be_bytes());
    }
    key
}

pub fn guess(seed: i64, server_seed: u64, depth: i32, data: &[u8]) -> Option<Zeroizing<Vec<u8>>> {
    // Attempt to generate the key.
    let mut generator = Random::seeded(seed as i32);
    for _ in 0..depth {
        let client_seed = generator.next_safe_uint64();

        let seed = client_seed ^ server_seed;
        let key = new_key_from_seed(seed);

        let mut clone = Zeroizing::new(data.to_vec());
        decrypt_command(&key, &mut clone);

        if clone.len() >= 4
            && clone[0] == 0x45
            && clone[1] == 0x67
            && clone[clone.len() - 2] == 0x89
            && clone[clone.len() - 1] == 0xAB
        {
            return Some(key);
        }
    }

    None
}

pub fn bruteforce(sent_time: u64, server_seed: u64, data: &[u8]) -> Option<Zeroizing<Vec<u8>>> {
    debug!("Running bruteforce loop.");
    // Generate new seeds.
    for i in 0..3000i64 {
        let offset = if i % 2 == 0 { i / 2 } else { -(i - 1) / 2 };
        let time = sent_time as i64 + offset; // This will act as the seed.

        if let Some(key) = guess(time, server_seed, 5, data) {
            return Some(key);
        }
    }
    warn!("Unable to find the encryption key seed.");
    None
}

#[cfg(test)]
mod tests {
    use sha2::{Digest, Sha256};

    use super::new_key_from_seed;

    #[test]
    fn generated_key_matches_fixed_public_seed_digest() {
        let key = new_key_from_seed(0x0123_4567_89ab_cdef);
        assert_eq!(key.len(), 4096);
        assert_eq!(
            format!("{:x}", Sha256::digest(&*key)),
            "178eb674152c801822ff41fe7f2b734f4c03050bc62383fe52a3d2e52e5dfb98"
        );
    }
}
