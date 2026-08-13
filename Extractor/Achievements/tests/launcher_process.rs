use std::process::Command;

#[test]
fn malformed_launcher_arguments_are_silent_and_never_reflected() {
    let secret = "https://secret.invalid/?token=DO_NOT_PRINT";
    let output = Command::new(env!("CARGO_BIN_EXE_pengo-achievements-launcher"))
        .args([
            "--launcher",
            "--game",
            "gi",
            "--kind",
            "achievements",
            "--job-id",
            "0123456789abcdef0123456789abcdef",
            "--cancel",
            "named-event",
            "--parent-watch",
            "named-mutex",
            "--ipc",
            "named-pipe",
            "--output-root",
            "downloads",
            "--url",
            secret,
        ])
        .output()
        .unwrap();
    assert_eq!(output.status.code(), Some(2));
    assert!(output.stdout.is_empty());
    assert!(output.stderr.is_empty());
}

#[test]
fn launcher_binary_has_no_standalone_or_help_surface() {
    for argument in ["--help", "--version"] {
        let output = Command::new(env!("CARGO_BIN_EXE_pengo-achievements-launcher"))
            .arg(argument)
            .output()
            .unwrap();
        assert_eq!(output.status.code(), Some(2));
        assert!(output.stdout.is_empty());
        assert!(output.stderr.is_empty());
    }
}

#[test]
fn standalone_help_remains_available() {
    let output = Command::new(env!("CARGO_BIN_EXE_pengo-achievements-live"))
        .arg("--help")
        .output()
        .unwrap();
    assert!(output.status.success());
    assert!(output.stderr.is_empty());
    assert!(String::from_utf8(output.stdout).unwrap().contains("Usage:"));
}
