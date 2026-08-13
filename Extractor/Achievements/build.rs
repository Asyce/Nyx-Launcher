use sha2::{Digest, Sha256};
use std::{collections::BTreeSet, env, fs, path::PathBuf};

fn catalog_ids(
    game: &str,
    expected_version: &str,
    expected_hash: &str,
    expected_count: usize,
) -> Vec<u32> {
    let path = PathBuf::from("../../Database/Achievements")
        .join(game)
        .join("catalog.json");
    println!("cargo:rerun-if-changed={}", path.display());
    let bytes =
        fs::read(&path).unwrap_or_else(|error| panic!("cannot read {}: {error}", path.display()));
    // Git may materialize the reviewed JSON with CRLF on Windows. Pin the
    // exact repository content while making that one transport-only newline
    // conversion irrelevant. Bare CR bytes are never accepted.
    assert!(
        !bytes
            .iter()
            .enumerate()
            .any(|(index, byte)| { *byte == b'\r' && bytes.get(index + 1) != Some(&b'\n') }),
        "{} contains a bare carriage return",
        path.display()
    );
    let normalized = bytes
        .split(|byte| *byte == b'\r')
        .flat_map(|part| part.iter().copied())
        .collect::<Vec<_>>();
    let actual_hash = format!("{:x}", Sha256::digest(&normalized));
    assert_eq!(
        actual_hash,
        expected_hash,
        "{} hash changed",
        path.display()
    );
    let value: serde_json::Value = serde_json::from_slice(&bytes)
        .unwrap_or_else(|error| panic!("invalid {}: {error}", path.display()));
    assert_eq!(
        value["catalogVersion"].as_str(),
        Some(expected_version),
        "{} version changed",
        path.display()
    );
    let rows = value["achievements"]
        .as_array()
        .unwrap_or_else(|| panic!("{} has no achievements array", path.display()));
    let mut seen = BTreeSet::new();
    for row in rows {
        let raw = row["id"]
            .as_str()
            .unwrap_or_else(|| panic!("{} contains a non-string id", path.display()));
        let id = raw
            .parse::<u32>()
            .unwrap_or_else(|_| panic!("{} contains an invalid id", path.display()));
        assert!(
            seen.insert(id),
            "{} contains duplicate id {id}",
            path.display()
        );
    }
    assert_eq!(
        seen.len(),
        expected_count,
        "{} count changed",
        path.display()
    );
    seen.into_iter().collect()
}

fn main() {
    if env::var("PROFILE").as_deref() == Ok("release")
        && env::var("CARGO_CFG_TARGET_OS").as_deref() == Ok("windows")
        && env::var("CARGO_CFG_TARGET_ENV").as_deref() == Ok("msvc")
    {
        // Make the Windows loader apply LOAD_LIBRARY_SEARCH_SYSTEM32 to every
        // direct PE dependency before Rust reaches main().
        println!("cargo:rustc-link-arg-bin=pengo-achievements-live=/DEPENDENTLOADFLAG:0x800");
        println!("cargo:rustc-link-arg-bin=pengo-achievements-launcher=/DEPENDENTLOADFLAG:0x800");
        println!("cargo:rustc-link-arg-bin=pengo-achievements-launcher=/Brepro");

        // A real embedded manifest makes Windows ignore executable-name
        // `.local` DLL redirection before Rust reaches main(). Keep this next
        // to the PE load flag: the two gates protect different loader paths.
        let manifest =
            PathBuf::from(env::var_os("CARGO_MANIFEST_DIR").expect("CARGO_MANIFEST_DIR missing"))
                .join("pengo-achievements-live.manifest");
        println!("cargo:rerun-if-changed={}", manifest.display());
        println!("cargo:rustc-link-arg-bin=pengo-achievements-live=/MANIFEST:EMBED");
        println!("cargo:rustc-link-arg-bin=pengo-achievements-launcher=/MANIFEST:EMBED");
        println!(
            "cargo:rustc-link-arg-bin=pengo-achievements-live=/MANIFESTINPUT:{}",
            manifest.display()
        );
        println!(
            "cargo:rustc-link-arg-bin=pengo-achievements-launcher=/MANIFESTINPUT:{}",
            manifest.display()
        );
    }
    let gi = catalog_ids(
        "gi",
        "6.7",
        "5608dd41a26a06639c6455d65de7abdd2a7e5e997f55c6ed93dec6d08dc673b5",
        1759,
    );
    let hsr = catalog_ids(
        "hsr",
        "4.4",
        "1686a1deb2a03e758e1047684acc9e760d5c793b2e2717bb4d1bc9eeb7c60502",
        1869,
    );
    let out = PathBuf::from(env::var_os("OUT_DIR").expect("OUT_DIR missing"));
    let source =
        format!("pub const GI_IDS: &[u32] = &{gi:?};\npub const HSR_IDS: &[u32] = &{hsr:?};\n");
    fs::write(out.join("catalog_ids.rs"), source).expect("cannot write embedded catalogs");
}
