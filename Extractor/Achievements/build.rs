use sha2::{Digest, Sha256};
use std::{
    collections::BTreeSet,
    env, fs,
    path::{Path, PathBuf},
};

pub(crate) fn catalog_data(
    path: &Path,
    expected_game: &str,
    expected_hash: &str,
    expected_count: usize,
) -> (String, Vec<u32>) {
    let bytes =
        fs::read(path).unwrap_or_else(|error| panic!("cannot read {}: {error}", path.display()));
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
    let object = value
        .as_object()
        .unwrap_or_else(|| panic!("{} must contain a JSON object", path.display()));
    let game = object.get("game").and_then(serde_json::Value::as_str);
    let version = object
        .get("catalogVersion")
        .and_then(serde_json::Value::as_str);
    let released_version = object
        .get("releasedVersion")
        .and_then(serde_json::Value::as_str);
    let valid_version = version.is_some_and(|version| {
        version.split_once('.').is_some_and(|(major, minor)| {
            [major, minor].into_iter().all(|part| {
                !part.is_empty()
                    && part.bytes().all(|byte| byte.is_ascii_digit())
                    && (part == "0" || !part.starts_with('0'))
            })
        })
    });
    assert!(
        game == Some(expected_game) && valid_version && released_version == version,
        "{} has an invalid game/catalogVersion/releasedVersion shape",
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
    (
        format!(
            "{expected_game}-{}",
            version.expect("validated version missing")
        ),
        seen.into_iter().collect(),
    )
}

pub(crate) fn generated_source(
    gi_version: &str,
    gi_ids: &[u32],
    hsr_version: &str,
    hsr_ids: &[u32],
) -> String {
    format!(
        "pub const GI_CATALOG_VERSION: &str = {gi_version:?};\n\
         pub const HSR_CATALOG_VERSION: &str = {hsr_version:?};\n\
         pub const GI_IDS: &[u32] = &{gi_ids:?};\n\
         pub const HSR_IDS: &[u32] = &{hsr_ids:?};\n"
    )
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
    let catalog_root = PathBuf::from("../../contracts");
    let gi_path = catalog_root.join("achievements-gi-catalog.json");
    let hsr_path = catalog_root.join("achievements-hsr-catalog.json");
    println!("cargo:rerun-if-changed={}", gi_path.display());
    println!("cargo:rerun-if-changed={}", hsr_path.display());
    let (gi_version, gi_ids) = catalog_data(
        &gi_path,
        "gi",
        "5608dd41a26a06639c6455d65de7abdd2a7e5e997f55c6ed93dec6d08dc673b5",
        1759,
    );
    let (hsr_version, hsr_ids) = catalog_data(
        &hsr_path,
        "hsr",
        "1686a1deb2a03e758e1047684acc9e760d5c793b2e2717bb4d1bc9eeb7c60502",
        1869,
    );
    let out = PathBuf::from(env::var_os("OUT_DIR").expect("OUT_DIR missing"));
    let source = generated_source(&gi_version, &gi_ids, &hsr_version, &hsr_ids);
    fs::write(out.join("catalog_ids.rs"), source).expect("cannot write embedded catalogs");
}
