# Upstream pin and Pengo patch

Exact crates.io source: `rand_mt` 4.2.2, registry checksum
`49e018c6ded60e5252609887c12eb3ca2592e9248c5894a7db3975c8a7a1e2df`, from
upstream commit `8d9c44fa58903d8fd86295c50513ec520d6c7678`.

The original source is MIT OR Apache-2.0 licensed. Pengo selects the retained
`LICENSE-MIT` text and adds only zeroization of generator state and index on
explicit wipe and drop. The local manifest pins `zeroize` 1.9.0 and raises the
declared minimum Rust version to that dependency's 1.85 requirement; the
upstream `Cargo.toml.orig` is retained unchanged. The upstream metadata-only
`version-sync` test and its test dependency are omitted so the security tests
remain reproducible on Nyx's pinned Rust 1.86 toolchain.
