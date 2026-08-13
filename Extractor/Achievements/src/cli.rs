use crate::Game;
use std::{path::PathBuf, time::Duration};

pub const JOB_ID_HEX_BYTES: usize = 32;

#[derive(Debug, Eq, PartialEq)]
pub enum Mode {
    Standalone,
    Launcher(LauncherOptions),
}

#[derive(Debug, Eq, PartialEq)]
pub enum OutputRoot {
    Downloads,
    Fixed(PathBuf),
}

#[derive(Debug, Eq, PartialEq)]
pub struct LauncherOptions {
    pub job_id: String,
    pub output_root: OutputRoot,
}

#[derive(Debug, Eq, PartialEq)]
pub struct Options {
    pub game: Game,
    pub timeout: Duration,
    pub mode: Mode,
}

pub enum Action {
    Run(Options),
    Help,
    Version,
}

pub fn parse<I, S>(arguments: I) -> Result<Action, String>
where
    I: IntoIterator<Item = S>,
    S: Into<String>,
{
    let mut values = arguments.into_iter().map(Into::into);
    let mut game = None;
    let mut timeout = Duration::from_secs(180);
    let mut launcher = false;
    let mut kind = None;
    let mut job_id = None;
    let mut cancel = None;
    let mut parent_watch = None;
    let mut ipc = None;
    let mut output_root = None;
    let mut fixed_root = None;
    while let Some(argument) = values.next() {
        match argument.as_str() {
            "--help" | "-h" => return Ok(Action::Help),
            "--version" | "-V" => return Ok(Action::Version),
            "--game" => {
                if game.is_some() {
                    return Err("--game was supplied twice".into());
                }
                game = Some(match values.next().as_deref() {
                    Some("gi") => Game::Gi,
                    Some("hsr") => Game::Hsr,
                    _ => return Err("--game must be gi or hsr".into()),
                });
            }
            "--launcher" => {
                if launcher {
                    return Err("--launcher was supplied twice".into());
                }
                launcher = true;
            }
            "--kind" => {
                if kind.is_some() {
                    return Err("--kind was supplied twice".into());
                }
                kind = Some(match values.next().as_deref() {
                    Some("achievements") => "achievements",
                    _ => return Err("--kind must be achievements".into()),
                });
            }
            "--job-id" => {
                if job_id.is_some() {
                    return Err("--job-id was supplied twice".into());
                }
                let value = values.next().ok_or("--job-id needs a value")?;
                if !valid_job_id(&value) {
                    return Err("--job-id is invalid".into());
                }
                job_id = Some(value);
            }
            "--cancel" => {
                if cancel.is_some() {
                    return Err("--cancel was supplied twice".into());
                }
                cancel = Some(match values.next().as_deref() {
                    Some("named-event") => "named-event",
                    _ => return Err("--cancel must be named-event".into()),
                });
            }
            "--parent-watch" => {
                if parent_watch.is_some() {
                    return Err("--parent-watch was supplied twice".into());
                }
                parent_watch = Some(match values.next().as_deref() {
                    Some("named-mutex") => "named-mutex",
                    _ => return Err("--parent-watch must be named-mutex".into()),
                });
            }
            "--ipc" => {
                if ipc.is_some() {
                    return Err("--ipc was supplied twice".into());
                }
                ipc = Some(match values.next().as_deref() {
                    Some("named-pipe") => "named-pipe",
                    _ => return Err("--ipc must be named-pipe".into()),
                });
            }
            "--output-root" => {
                if output_root.is_some() {
                    return Err("--output-root was supplied twice".into());
                }
                output_root = Some(match values.next().as_deref() {
                    Some("downloads") => "downloads",
                    Some("fixed") => "fixed",
                    _ => return Err("--output-root must be downloads or fixed".into()),
                });
            }
            "--fixed-root" => {
                if fixed_root.is_some() {
                    return Err("--fixed-root was supplied twice".into());
                }
                fixed_root = Some(PathBuf::from(
                    values.next().ok_or("--fixed-root needs a path")?,
                ));
            }
            "--timeout-seconds" => {
                let seconds = values
                    .next()
                    .ok_or("--timeout-seconds needs a number")?
                    .parse::<u64>()
                    .map_err(|_| "--timeout-seconds needs a number")?;
                if !(30..=300).contains(&seconds) {
                    return Err("--timeout-seconds must be from 30 to 300".into());
                }
                timeout = Duration::from_secs(seconds);
            }
            _ => return Err("unknown option".into()),
        }
    }
    let game = game.ok_or("--game is required (gi or hsr)")?;
    let mode = if launcher {
        if kind != Some("achievements") {
            return Err("--kind achievements is required in launcher mode".into());
        }
        if cancel != Some("named-event") {
            return Err("--cancel named-event is required in launcher mode".into());
        }
        if parent_watch != Some("named-mutex") {
            return Err("--parent-watch named-mutex is required in launcher mode".into());
        }
        if ipc != Some("named-pipe") {
            return Err("--ipc named-pipe is required in launcher mode".into());
        }
        let job_id = job_id.ok_or("--job-id is required in launcher mode")?;
        let output_root = match (output_root, fixed_root) {
            (Some("downloads"), None) => OutputRoot::Downloads,
            (Some("fixed"), Some(path)) => OutputRoot::Fixed(path),
            (Some("downloads"), Some(_)) => {
                return Err("--fixed-root is not allowed for Downloads".into());
            }
            (Some("fixed"), None) => return Err("--fixed-root is required".into()),
            _ => return Err("--output-root is required in launcher mode".into()),
        };
        Mode::Launcher(LauncherOptions {
            job_id,
            output_root,
        })
    } else {
        if kind.is_some()
            || job_id.is_some()
            || cancel.is_some()
            || parent_watch.is_some()
            || ipc.is_some()
            || output_root.is_some()
            || fixed_root.is_some()
        {
            return Err("launcher-only option".into());
        }
        Mode::Standalone
    };
    Ok(Action::Run(Options {
        game,
        timeout,
        mode,
    }))
}

fn valid_job_id(value: &str) -> bool {
    value.len() == JOB_ID_HEX_BYTES
        && value
            .bytes()
            .all(|byte| byte.is_ascii_digit() || matches!(byte, b'a'..=b'f'))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_safe_defaults_and_explicit_values() {
        let Action::Run(defaults) = parse(["--game", "gi"]).unwrap() else {
            panic!("expected run");
        };
        assert_eq!(defaults.game, Game::Gi);
        assert_eq!(defaults.timeout, Duration::from_secs(180));
        assert_eq!(defaults.mode, Mode::Standalone);

        let Action::Run(custom) = parse(["--game", "hsr", "--timeout-seconds", "30"]).unwrap()
        else {
            panic!("expected run");
        };
        assert_eq!(custom.game, Game::Hsr);
        assert_eq!(custom.timeout, Duration::from_secs(30));
        assert_eq!(custom.mode, Mode::Standalone);
    }

    #[test]
    fn parses_only_the_fixed_launcher_contract() {
        let Action::Run(options) = parse([
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
        ])
        .unwrap() else {
            panic!("expected run");
        };
        assert_eq!(
            options.mode,
            Mode::Launcher(LauncherOptions {
                job_id: "0123456789abcdef0123456789abcdef".into(),
                output_root: OutputRoot::Downloads,
            })
        );

        let Action::Run(options) = parse([
            "--launcher",
            "--game",
            "hsr",
            "--kind",
            "achievements",
            "--job-id",
            "abcdef0123456789abcdef0123456789",
            "--cancel",
            "named-event",
            "--parent-watch",
            "named-mutex",
            "--ipc",
            "named-pipe",
            "--output-root",
            "fixed",
            "--fixed-root",
            r"C:\safe\exports",
        ])
        .unwrap() else {
            panic!("expected run");
        };
        assert_eq!(
            options.mode,
            Mode::Launcher(LauncherOptions {
                job_id: "abcdef0123456789abcdef0123456789".into(),
                output_root: OutputRoot::Fixed(PathBuf::from(r"C:\safe\exports")),
            })
        );
    }

    #[test]
    fn rejects_missing_duplicate_and_unknown_options() {
        assert!(parse::<_, String>([]).is_err());
        assert!(parse(["--game", "zzz"]).is_err());
        assert!(parse(["--game", "gi", "--game", "hsr"]).is_err());
        assert!(parse(["--game", "gi", "--wat"]).is_err());
        assert!(parse(["--game", "gi", "--output", "mine.json"]).is_err());
        assert!(parse(["--game", "gi", "--force"]).is_err());
        assert!(parse(["--game", "gi", "--timeout-seconds", "301"]).is_err());
    }

    #[test]
    fn launcher_rejects_injection_and_ambiguous_roots() {
        let base = [
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
        ];
        for extra in [
            ["--url", "https://secret.invalid/?token=hidden"],
            ["--provider", "cmd.exe"],
            ["--command", "whoami"],
            ["--output", r"C:\arbitrary.json"],
        ] {
            assert!(parse(base.into_iter().chain(extra)).is_err());
        }
        assert!(
            parse([
                "--launcher",
                "--game",
                "gi",
                "--kind",
                "achievements",
                "--job-id",
                "../escape",
                "--cancel",
                "named-event",
                "--output-root",
                "downloads",
            ])
            .is_err()
        );
        assert!(parse(base.into_iter().chain(["--fixed-root", r"C:\other"])).is_err());
    }
}
