#![windows_subsystem = "windows"]

use pengo_achievements_live::{
    cli::{self, Action, Mode, Options},
    launcher::{ErrorCode, StatusWriter},
    launcher_app, security,
};
use std::panic::{AssertUnwindSafe, catch_unwind};

fn main() {
    if security::harden_process_dll_search().is_err() {
        std::process::exit(1);
    }
    security::set_launcher_mode();
    security::install_safe_panic_hook();
    let action = match cli::parse(std::env::args().skip(1)) {
        Ok(action) => action,
        Err(_) => std::process::exit(2),
    };
    let Action::Run(Options {
        game,
        timeout,
        mode: Mode::Launcher(options),
    }) = action
    else {
        std::process::exit(2);
    };
    let mut statuses = match StatusWriter::connect(options.job_id.clone(), game) {
        Ok(statuses) => statuses,
        Err(_) => std::process::exit(1),
    };
    match catch_unwind(AssertUnwindSafe(|| {
        launcher_app::run(game, timeout, options, &mut statuses)
    })) {
        Ok(Ok(())) => {}
        Ok(Err(_)) => std::process::exit(1),
        Err(_) => {
            let _ = statuses.failed(ErrorCode::InternalError);
            std::process::exit(1);
        }
    }
}
