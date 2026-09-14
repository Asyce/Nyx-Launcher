#[allow(dead_code)]
#[path = "../build.rs"]
mod build_script;

use sha2::{Digest, Sha256};

fn catalog(path: &std::path::Path, game: &str, version: &str, released: &str) -> String {
    let json = format!(
        "{{\"game\":\"{game}\",\"catalogVersion\":\"{version}\",\"releasedVersion\":\"{released}\",\"achievements\":[{{\"id\":\"1\"}}]}}"
    );
    std::fs::write(path, &json).unwrap();
    format!("{:x}", Sha256::digest(json.as_bytes()))
}

#[test]
fn changed_catalog_versions_flow_into_generated_rust_constants() {
    let root = tempfile::tempdir().unwrap();
    let gi_path = root.path().join("gi.json");
    let hsr_path = root.path().join("hsr.json");
    let gi_hash = catalog(&gi_path, "gi", "9.9", "9.9");
    let hsr_hash = catalog(&hsr_path, "hsr", "8.8", "8.8");
    let (gi_version, gi_ids) = build_script::catalog_data(&gi_path, "gi", &gi_hash, 1);
    let (hsr_version, hsr_ids) = build_script::catalog_data(&hsr_path, "hsr", &hsr_hash, 1);

    let source = build_script::generated_source(&gi_version, &gi_ids, &hsr_version, &hsr_ids);

    assert!(source.contains("GI_CATALOG_VERSION: &str = \"gi-9.9\""));
    assert!(source.contains("HSR_CATALOG_VERSION: &str = \"hsr-8.8\""));
}

#[test]
fn wrong_game_or_released_version_fails_closed() {
    let root = tempfile::tempdir().unwrap();
    for (game, version, released) in [("hsr", "9.9", "9.9"), ("gi", "9.9", "9.8")] {
        let path = root.path().join(format!("{game}-{released}.json"));
        let hash = catalog(&path, game, version, released);
        assert!(
            std::panic::catch_unwind(|| { build_script::catalog_data(&path, "gi", &hash, 1) })
                .is_err()
        );
    }
}
