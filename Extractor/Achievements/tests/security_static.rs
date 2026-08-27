use sha2::{Digest, Sha256};
use std::{fs, path::Path};

fn read(path: impl AsRef<Path>) -> String {
    fs::read_to_string(path).unwrap()
}

fn hash(path: impl AsRef<Path>) -> String {
    let bytes = fs::read(path).unwrap();
    assert!(
        !bytes
            .iter()
            .enumerate()
            .any(|(index, byte)| *byte == b'\r' && bytes.get(index + 1) != Some(&b'\n'))
    );
    let normalized = bytes
        .into_iter()
        .filter(|byte| *byte != b'\r')
        .collect::<Vec<_>>();
    format!("{:x}", Sha256::digest(normalized))
}

#[test]
fn dependency_commits_and_realtime_only_backend_are_pinned() {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"));
    let manifest = read(root.join("Cargo.toml"));
    let capture = read(root.join("vendor/pktmon-realtime/src/lib.rs"));
    let artifactarium = read(root.join("vendor/auto-artifactarium/UPSTREAM.md"));
    let reliquary = read(root.join("vendor/auto-reliquary/UPSTREAM.md"));
    let kcp = read(root.join("vendor/mhy-kcp/UPSTREAM.md"));
    assert!(manifest.contains("path = \"vendor/auto-artifactarium\""));
    assert!(manifest.contains("path = \"vendor/auto-reliquary\""));
    assert!(manifest.contains("[patch.\"https://github.com/hashblen/mhy-kcp\"]"));
    assert!(artifactarium.contains("04421c4f8a7ed7e7b65bb5e6e59231d4e98405cf"));
    assert!(reliquary.contains("bc23b48cb3b1b994a5d4405cefea42eb0e1d3735"));
    assert!(kcp.contains("1acf4ba5938ff91f7f2d2a31e16bf1f8d2db9c8f"));
    assert_eq!(
        hash(root.join("vendor/mhy-kcp/src/error.rs")),
        "1adfe0acf36dec662342553bbad445cd7b73bd6ec2887c8df1bd62378c883882"
    );
    assert_eq!(
        hash(root.join("vendor/mhy-kcp/src/kcp.rs")),
        "cec925edfe680c2e93e4ac33600c4a1e79e4ba69d54608de58b9c83ee9ae6108"
    );
    assert_eq!(
        hash(root.join("vendor/mhy-kcp/src/lib.rs")),
        "c944e0d039f55a6f04e0ff94ea59f198ebfd997deccefae45b0fd92203b1e26b"
    );
    assert!(!capture.contains("mod legacy"));
    assert!(!capture.contains("LegacyBackend"));
    assert!(!capture.contains("EtlCapture"));
}

#[test]
fn packaged_notices_cover_the_locked_dependency_graph() {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"));
    let lock = read(root.join("Cargo.lock"));
    let notices = read(root.join("THIRD_PARTY_NOTICES.md")).replace("\r\n", "\n");

    let lock_hash = hash(root.join("Cargo.lock"));
    assert!(notices.contains(&lock_hash));
    assert!(notices.contains("| `subtle` | `2.6.1` | BSD-3-Clause |"));
    assert!(notices.contains("| `unicode-ident` | `1.0.24` | MIT AND Unicode-3.0 |"));
    assert!(notices.contains("Copyright (c) 2015 Andrew Gallant"));
    assert!(notices.contains("Copyright (c) 2018 Carl Lerche"));
    assert!(notices.contains("Copyright © 1991-2023 Unicode, Inc."));
    assert!(notices.contains("Copyright (c) 2016-2024 Isis Agora Lovecruft"));
    assert!(notices.contains("`202292262c82845c1d1c02695a86123e09f7eb4ef08826bcc187b16f5f9185f3`"));
    assert!(notices.contains("`6c51d31f2009cda50d5112c2870e4a4d37696f2d5b29fa601b5ef186f4a5d11c`"));
    for required_notice in [
        "1e2b7ade3fb228130408b9990cae6a7618eb314c75aa0b164bfe485d9d9756ee",
        "3290ae0fbc9ddb77d2239121d710f0bb9d31b3b4744e6d97fe01e652b4c1870b",
        "377c2e7c53250cc5905c0b0532d35973392af16ffb9596a41d99d202cf3617c9",
        "74db5baf44a41b1000312c673544b3374e4198af5605c7f9080a402cec42cfa3",
        "90eb64f0279b0d9432accfa6023ff803bc4965212383697eee27a0f426d5f8d5",
    ] {
        assert!(notices.contains(&format!("### `{required_notice}`")));
    }
    assert!(notices.contains("| `regex-syntax` | `0.8.11` | src/unicode_tables/LICENSE-UNICODE | `74db5baf44a41b1000312c673544b3374e4198af5605c7f9080a402cec42cfa3` |"));
    assert!(notices.contains("| `tracing-core` | `0.1.36` | src/spin/LICENSE | `58545fed1565e42d687aecec6897d35c6d37ccb71479a137c0deb2203e125c79` |"));

    let mut license_block_count = 0;
    for block in notices.split("\n### `").skip(1) {
        let (expected_hash, body) = block.split_once("`\n").unwrap();
        assert_eq!(expected_hash.len(), 64);
        assert!(expected_hash.bytes().all(|byte| byte.is_ascii_hexdigit()));
        let (_, fenced) = body.split_once("```text\n").unwrap();
        let (license_text, _) = fenced.split_once("```\n").unwrap();
        assert_eq!(format!("{:x}", Sha256::digest(license_text)), expected_hash);
        license_block_count += 1;
    }
    assert!(license_block_count > 50);

    let mut dependency_count = 0;
    for package in lock.split("[[package]]").skip(1) {
        let name = package
            .lines()
            .find_map(|line| {
                line.strip_prefix("name = \"")
                    .and_then(|line| line.strip_suffix('"'))
            })
            .unwrap();
        if name == "pengo-achievements-live" {
            continue;
        }
        let version = package
            .lines()
            .find_map(|line| {
                line.strip_prefix("version = \"")
                    .and_then(|line| line.strip_suffix('"'))
            })
            .unwrap();
        assert!(
            notices.contains(&format!("| `{name}` | `{version}` |")),
            "missing locked dependency notice for {name} {version}"
        );
        dependency_count += 1;
    }
    assert!(dependency_count > 100);

    let inventory = notices
        .split_once("## Locked Rust dependency inventory")
        .unwrap()
        .1
        .split_once("## Additional required notices and copyright files")
        .unwrap()
        .0;
    let package_rows = inventory
        .lines()
        .filter(|line| line.starts_with("| `") && line.contains(" | `"))
        .count();
    assert_eq!(package_rows, dependency_count);
    for line in notices.lines().filter(|line| line.starts_with("| `")) {
        for value in line.split('`').skip(1).step_by(2) {
            if value.len() == 64 && value.bytes().all(|byte| byte.is_ascii_hexdigit()) {
                assert!(
                    notices.contains(&format!("### `{value}`")),
                    "missing exact license-text block {value}"
                );
            }
        }
    }
}

#[test]
fn application_has_no_forbidden_high_risk_capabilities() {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"));
    let application = [
        read(root.join("Cargo.toml")),
        read(root.join("src/main.rs")),
        read(root.join("src/capture.rs")),
        read(root.join("src/decoder.rs")),
        read(root.join("src/launcher.rs")),
        read(root.join("src/launcher_app.rs")),
        read(root.join("src/bin/pengo-achievements-launcher.rs")),
        read(root.join("src/npcap.rs")),
        read(root.join("src/output.rs")),
        read(root.join("src/security.rs")),
    ]
    .join("\n");
    for forbidden in [
        "reqwest",
        "ureq",
        "arboard",
        "ReadProcessMemory",
        "WriteProcessMemory",
        "CreateRemoteThread",
        "SetWindowsHookEx",
        "SendInput",
        "keybd_event",
        "mouse_event",
        "std::process::Command",
        "ShellExecute",
        "CreateProcess",
    ] {
        assert!(
            !application.contains(forbidden),
            "found forbidden capability: {forbidden}"
        );
    }
    assert!(application.contains("IsUserAnAdmin"));
    assert!(application.contains("I UNDERSTAND"));
}

#[test]
fn npcap_fallback_is_pinned_narrow_and_non_elevated() {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"));
    let npcap = read(root.join("src/npcap.rs"));
    let capture = read(root.join("src/capture.rs"));
    assert!(npcap.contains("Npcap version 1.88, based on libpcap version 1.10.6 (64-bit time_t)"));
    for hash in [
        "D1CA7FCF9128D02A75EAF29CE9A9D85C5697377460F92420D976DA187521CF39",
        "2793CE72F0E04D5885AAEE1273A7373441D01934B2CFF3886B031C13CA826345",
        "13D598E277E9C7BF43688D7087EF9B944E8036561A1E7169D31D9EC1D38F9A01",
    ] {
        assert!(npcap.contains(hash));
    }
    assert!(npcap.contains("GetSystemDirectoryW"));
    assert!(npcap.contains("join(\"Npcap\")"));
    assert!(npcap.contains("LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32"));
    assert!(npcap.contains("preloaded_module(w!(\"wpcap.dll\"))"));
    assert!(npcap.contains("preloaded_module(w!(\"packet.dll\"))"));
    assert!(npcap.contains("IsUserAnAdmin().as_bool()"));
    assert!(npcap.contains("GetBestInterface"));
    assert!(npcap.contains("OpenSCManagerW"));
    assert!(npcap.contains("OpenServiceW"));
    assert!(npcap.contains("QueryServiceStatusEx"));
    assert!(npcap.contains("SERVICE_RUNNING"));
    assert!(npcap.contains("SERVICE_SYSTEM_START"));
    assert!(npcap.contains(r#"\SystemRoot\system32\DRIVERS\npcap.sys"#));
    assert!(npcap.contains("DLT_EN10MB"));
    assert!(npcap.contains("udp and (port 22101 or port 22102)"));
    assert!(npcap.contains("udp and (port 23301 or port 23302)"));
    assert!(npcap.contains("SNAPLEN: c_int = 9_000"));
    assert!(npcap.contains("KERNEL_BUFFER_BYTES: c_int = 1024 * 1024"));
    assert!(npcap.contains("api.set_promisc(handle, 0)"));
    assert!(npcap.contains("set_nonblock"));
    assert!(npcap.contains("thread::sleep"));
    assert!(!npcap.contains("thread::spawn"));
    assert!(npcap.contains("every_setup_failure_closes_and_frees_exactly_once"));
    assert!(npcap.contains("actual_compiled_filters_accept_only_the_selected_games_udp_frames"));
    assert!(npcap.contains("pcap_offline_filter"));
    for forbidden in [
        "pcap_findalldevs",
        "pcap_dump",
        "pcap_sendpacket",
        "pcap_open_offline",
        "pcap_setdirection",
    ] {
        assert!(!npcap.contains(forbidden), "Npcap retained {forbidden}");
    }
    assert!(capture.contains("BackendSelectionError::NpcapRejectsAdministrator"));
}

#[test]
fn memory_queue_and_output_location_are_bounded() {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"));
    let realtime = read(root.join("vendor/pktmon-realtime/src/realtime/mod.rs"));
    let output = read(root.join("src/output.rs"));
    assert!(realtime.contains("sync_channel(1024)"));
    assert!(realtime.contains("try_send(packet)"));
    assert!(output.contains("SHGetKnownFolderPath"));
    assert!(output.contains("FOLDERID_LocalAppData"));
    assert!(output.contains("FOLDERID_Downloads"));
    assert!(output.contains("Pengo Exports"));
    assert!(output.contains("GetDriveTypeW"));
    assert!(output.contains("FILE_ATTRIBUTE_REPARSE_POINT"));
    assert!(output.contains("FILE_SHARE_READ.0 | FILE_SHARE_WRITE.0"));
    assert!(output.contains("FlushFileBuffers"));
    assert!(output.contains("MoveFileExW"));
    assert!(!output.contains("MOVEFILE_REPLACE_EXISTING"));
}

#[test]
fn launcher_protocol_is_fixed_redacted_and_noninteractive() {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"));
    let cli = read(root.join("src/cli.rs"));
    let launcher = read(root.join("src/launcher.rs"));
    let main = read(root.join("src/main.rs"));
    let launcher_bin = read(root.join("src/bin/pengo-achievements-launcher.rs"));
    for required in [
        "achievements",
        "named-event",
        "downloads",
        "fixed",
        "named-pipe",
        "JOB_ID_HEX_BYTES",
    ] {
        assert!(cli.contains(required));
    }
    for forbidden in ["--url", "--provider", "--command"] {
        assert!(!main.contains(forbidden));
        assert!(!launcher.contains(forbidden));
    }
    for state in [
        "preparing",
        "ready",
        "waiting_for_game",
        "exported",
        "failed",
        "cancelled",
    ] {
        assert!(launcher.contains(state));
    }
    assert!(launcher.contains("OpenEventW"));
    assert!(launcher.contains("OpenMutexW"));
    assert!(launcher.contains("Pengo.Nyx.ExportParent.v1"));
    assert!(launcher.contains("WaitForSingleObject"));
    assert!(launcher.contains("capture_invalid_snapshot"));
    assert!(!launcher.contains("eprintln!"));
    assert!(!launcher.contains("println!"));
    assert!(main.contains("security::set_launcher_mode()"));
    assert!(launcher_bin.contains("windows_subsystem = \"windows\""));
    assert!(!launcher_bin.contains("print_help"));
}

#[test]
fn system_dll_loading_and_cancellation_have_single_safe_owners() {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"));
    let loader = read(root.join("vendor/pktmon-realtime/src/realtime/c_api.rs"));
    let realtime = read(root.join("vendor/pktmon-realtime/src/realtime/mod.rs"));
    let main = read(root.join("src/main.rs"));
    let security = read(root.join("src/security.rs"));
    assert!(loader.contains("GetSystemDirectoryW"));
    assert!(loader.contains("LoadLibraryExW"));
    assert!(loader.contains("LOAD_LIBRARY_SEARCH_SYSTEM32"));
    assert!(loader.contains("GetModuleFileNameW"));
    assert!(!loader.contains("LoadLibraryA"));
    assert!(security.contains("SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32)"));
    assert!(!security.contains("LOAD_LIBRARY_SEARCH_USER_DIRS"));
    assert!(!security.contains("AddDllDirectory"));
    let main_body = main.split_once("fn main() {").unwrap().1.trim_start();
    assert!(main_body.starts_with("let hardening = security::harden_process_dll_search();"));
    let hardening = main.find("harden_process_dll_search()").unwrap();
    let panic_hook = main.find("install_safe_panic_hook()").unwrap();
    let arguments = main.find("cli::parse").unwrap();
    assert!(hardening < panic_hook && panic_hook < arguments);
    assert!(!realtime.contains("install_shutdown_hook"));
    assert!(!realtime.contains("SetConsoleCtrlHandler"));
    let handler = main.find("cancellation_flag()").unwrap();
    let capture = main.find("RealTimeFrameSource::new").unwrap();
    assert!(handler < capture);
}

#[test]
fn realtime_ffi_and_partial_construction_are_fail_closed() {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"));
    let api = read(root.join("vendor/pktmon-realtime/src/realtime/c_api.rs"));
    let realtime = read(root.join("vendor/pktmon-realtime/src/realtime/mod.rs"));
    assert!(api.contains("event_kind: u32"));
    assert!(!api.contains("enum PacketMonitorStreamEventKind"));
    let last_export = api
        .find("let add_capture_constraint = get_proc_address!")
        .unwrap();
    let disarm = api.find("let module = module_guard").unwrap();
    assert!(last_export < disarm);
    assert!(realtime.contains("checked_event_kind"));
    assert!(realtime.contains("BackendConstructionGuard"));
    assert!(realtime.contains("partial_construction_cleans_up_in_reverse_order"));
}

#[test]
fn release_startup_dependencies_are_hardened_before_main() {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"));
    let cargo_config = read(root.join(".cargo/config.toml"));
    let build = read(root.join("build.rs"));
    let manifest = read(root.join("pengo-achievements-live.manifest"));
    let verifier = read(root.join("tools/verify_release.py"));
    assert!(cargo_config.contains("[target.x86_64-pc-windows-msvc]"));
    assert!(cargo_config.contains("target-feature=+crt-static"));
    assert!(build.contains("env::var(\"PROFILE\").as_deref() == Ok(\"release\")"));
    assert!(build.contains("/DEPENDENTLOADFLAG:0x800"));
    assert!(build.contains("rustc-link-arg-bin=pengo-achievements-launcher"));
    assert!(build.contains("pengo-achievements-launcher=/Brepro"));
    assert!(build.contains("/MANIFEST:EMBED"));
    assert!(build.contains("/MANIFESTINPUT:"));
    assert!(manifest.contains("Pengo.AchievementExtractor"));
    assert!(manifest.contains("requestedExecutionLevel level=\"asInvoker\""));
    assert!(verifier.contains("dependent_load_flags != 0x800"));
    assert!(verifier.contains("RT_MANIFEST"));
    assert!(verifier.contains("no-console GUI subsystem"));
    assert!(verifier.contains("forbidden_module == \"vcruntime140.dll\""));
    assert!(verifier.contains("bcryptprimitives.dll"));
}

#[test]
fn parser_forks_cannot_print_or_trace_captured_secrets() {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"));
    let parsers = [
        read(root.join("vendor/auto-artifactarium/src/lib.rs")),
        read(root.join("vendor/auto-artifactarium/src/crypto.rs")),
        read(root.join("vendor/auto-artifactarium/src/kcp.rs")),
        read(root.join("vendor/auto-artifactarium/src/unk_util.rs")),
        read(root.join("vendor/auto-reliquary/src/lib.rs")),
        read(root.join("vendor/auto-reliquary/src/crypto.rs")),
        read(root.join("vendor/auto-reliquary/src/kcp.rs")),
    ]
    .join("\n");
    for forbidden in [
        "bytes_as_hex",
        "println!(\"field",
        "BASE64_STANDARD.encode(&command.proto_data)",
        "Found encryption key seed",
        "possible session seeds",
        "setting new session seed",
        "before decryption",
        "after decryption",
        "message data:",
    ] {
        assert!(
            !parsers.contains(forbidden),
            "parser retained sensitive output: {forbidden}"
        );
    }
    let artifactarium_manifest = read(root.join("vendor/auto-artifactarium/Cargo.toml"));
    let reliquary_manifest = read(root.join("vendor/auto-reliquary/Cargo.toml"));
    let pktmon_manifest = read(root.join("vendor/pktmon-realtime/Cargo.toml"));
    assert!(artifactarium_manifest.contains("release_max_level_off"));
    assert!(reliquary_manifest.contains("release_max_level_off"));
    assert!(pktmon_manifest.contains("release_max_level_off"));
}
