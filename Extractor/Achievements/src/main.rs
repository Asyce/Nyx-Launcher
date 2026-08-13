use pengo_achievements_live::{
    Game,
    capture::{
        BackendChoice, BackendSelectionError, CaptureLimits, FrameSource, RealTimeFrameSource,
        cancellation_flag, capture_complete_snapshot, choose_backend,
    },
    cli::{self, Action, Mode, Options},
    decoder::GameDecoder,
    launcher::{ErrorCode, StatusWriter},
    launcher_app,
    npcap::NpcapFrameSource,
    output::write_export,
    security,
};
use std::{
    io::{self, Write},
    panic::{AssertUnwindSafe, catch_unwind},
};

const ACCEPTANCE: &str = "I UNDERSTAND";

fn is_administrator() -> bool {
    unsafe { windows::Win32::UI::Shell::IsUserAnAdmin().as_bool() }
}

fn print_help() {
    println!(
        "Pengo achievement extractor (branch-only test build)\n\n\
Usage:\n  pengo-achievements-live --game gi|hsr [--timeout-seconds 30..300]\n\n\
Run PowerShell normally for either game; the reviewed Npcap path refuses elevation. Start the extractor first, then launch the chosen game fresh and enter the world."
    );
}

fn confirm_risk(game: Game, backend: BackendChoice) -> Result<(), String> {
    println!("This temporary test tool watches only the game's two UDP ports in memory.");
    println!(
        "It does not open the game, read its memory, press keys, save packets, log in, or use the internet."
    );
    println!(
        "It still watches game traffic using {}. That may break the game's rules. Use it only if you accept that risk.",
        match backend {
            BackendChoice::PktMon => "Windows Packet Monitor as Administrator",
            BackendChoice::Npcap => {
                "the reviewed Npcap installation without Administrator access"
            }
        }
    );
    if game == Game::Hsr {
        println!(
            "HSR packet capture is experimental. The official HoYoLAB route is safer when available."
        );
    }
    print!("Type {ACCEPTANCE} to continue: ");
    io::stdout()
        .flush()
        .map_err(|_| "could not show the prompt")?;
    let mut answer = String::new();
    io::stdin()
        .read_line(&mut answer)
        .map_err(|_| "could not read the confirmation")?;
    if answer.trim() != ACCEPTANCE {
        return Err("cancelled; nothing was captured or written".into());
    }
    Ok(())
}

fn run(options: Options) -> Result<(), String> {
    let administrator = is_administrator();
    let backend = choose_backend(options.game, administrator).map_err(|error| {
        match error {
            BackendSelectionError::AdministratorRequired => "Administrator access is required for Windows Packet Monitor. Right-click PowerShell, choose Run as administrator, then run this command again.",
            BackendSelectionError::NpcapRejectsAdministrator => "This Windows build needs the reviewed Npcap fallback, which refuses Administrator mode. Close this window and run the command from a normal PowerShell.",
        }
    })?;
    confirm_risk(options.game, backend)?;
    println!(
        "Ready. Now launch {} fresh and enter {}. Press Ctrl+C to cancel.",
        match options.game {
            Game::Gi => "Genshin Impact",
            Game::Hsr => "Honkai: Star Rail",
        },
        match options.game {
            Game::Gi => "the world through the door",
            Game::Hsr => "the game from the train screen",
        }
    );
    let cancelled = cancellation_flag().map_err(|_| "Ctrl+C protection could not be installed")?;
    let mut decoder = GameDecoder::new(options.game)?;
    let mut source: Box<dyn FrameSource> = match backend {
        BackendChoice::PktMon => Box::new(
            RealTimeFrameSource::new(options.game.ports()).map_err(|error| error.to_string())?,
        ),
        BackendChoice::Npcap => Box::new(NpcapFrameSource::new(options.game)?),
    };
    let completed = capture_complete_snapshot(
        source.as_mut(),
        &mut decoder,
        options.game,
        options.game.released_ids(),
        options.game.other_ids(),
        CaptureLimits {
            timeout: options.timeout,
            ..CaptureLimits::default()
        },
        cancelled.as_ref(),
    )
    .map_err(|error| error.to_string())?;
    write_export(options.game, &completed).map_err(|error| error.to_string())?;
    println!("Saved {} completed achievements.", completed.len());
    Ok(())
}

fn main() {
    let hardening = security::harden_process_dll_search();
    let arguments = std::env::args().skip(1).collect::<Vec<_>>();
    let launcher_intent = arguments.iter().any(|argument| argument == "--launcher");
    if hardening.is_err() {
        if !launcher_intent {
            eprintln!("Error: Windows security setup failed; nothing was captured or written.");
        }
        std::process::exit(1);
    }
    if launcher_intent {
        security::set_launcher_mode();
    }
    security::install_safe_panic_hook();
    let action = match cli::parse(arguments) {
        Ok(action) => action,
        Err(error) => {
            if !launcher_intent {
                eprintln!("Error: {error}\nUse --help for the simple instructions.");
            }
            std::process::exit(2);
        }
    };
    if launcher_intent
        && !matches!(
            &action,
            Action::Run(Options {
                mode: Mode::Launcher(_),
                ..
            })
        )
    {
        std::process::exit(2);
    }
    let result = match action {
        Action::Help => {
            print_help();
            Ok(())
        }
        Action::Version => {
            println!("{}", env!("CARGO_PKG_VERSION"));
            Ok(())
        }
        Action::Run(Options {
            game,
            timeout,
            mode: Mode::Launcher(options),
        }) => {
            let mut statuses = match StatusWriter::connect(options.job_id.clone(), game) {
                Ok(statuses) => statuses,
                Err(_) => std::process::exit(1),
            };
            match catch_unwind(AssertUnwindSafe(|| {
                launcher_app::run(game, timeout, options, &mut statuses)
            })) {
                Ok(Ok(())) => return,
                Ok(Err(_)) => std::process::exit(1),
                Err(_) => {
                    let _ = statuses.failed(ErrorCode::InternalError);
                    std::process::exit(1);
                }
            }
        }
        Action::Run(options) => run(options),
    };
    if let Err(error) = result {
        eprintln!("Error: {error}");
        std::process::exit(1);
    }
}
