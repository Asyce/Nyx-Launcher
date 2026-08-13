use crate::{
    Game,
    capture::{
        BackendChoice, BackendSelectionError, CancelSignal, CaptureLimits, FrameSource,
        RealTimeFrameSource, capture_complete_snapshot, choose_backend,
    },
    cli::LauncherOptions,
    decoder::GameDecoder,
    launcher::{self, CancelEvent, ErrorCode, State, StatusWriter},
    npcap::NpcapFrameSource,
    output::{OutputError, prepare_launcher_output, write_launcher_export},
};
use std::{io::Write, time::Duration};

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct LauncherRunError;

fn is_administrator() -> bool {
    unsafe { windows::Win32::UI::Shell::IsUserAnAdmin().as_bool() }
}

fn fail<W: Write>(statuses: &mut StatusWriter<W>, code: ErrorCode) -> Result<(), LauncherRunError> {
    statuses.failed(code).map_err(|_| LauncherRunError)?;
    Err(LauncherRunError)
}

pub fn run<W: Write>(
    game: Game,
    timeout: Duration,
    options: LauncherOptions,
    statuses: &mut StatusWriter<W>,
) -> Result<(), LauncherRunError> {
    statuses
        .state(State::Preparing)
        .map_err(|_| LauncherRunError)?;
    let backend = match choose_backend(game, is_administrator()) {
        Ok(backend) => backend,
        Err(BackendSelectionError::AdministratorRequired) => {
            return fail(statuses, ErrorCode::AdministratorRequired);
        }
        Err(BackendSelectionError::NpcapRejectsAdministrator) => {
            return fail(statuses, ErrorCode::NormalUserRequired);
        }
    };
    let cancelled = match CancelEvent::open(&options.job_id) {
        Ok(cancelled) => cancelled,
        Err(_) => return fail(statuses, ErrorCode::CancelUnavailable),
    };
    if cancelled.is_cancelled() {
        statuses
            .state(State::Cancelled)
            .map_err(|_| LauncherRunError)?;
        return Err(LauncherRunError);
    }
    let output = match prepare_launcher_output(&options.output_root, game) {
        Ok(output) => output,
        Err(error) => return fail(statuses, launcher::output_error_code(&error)),
    };
    let mut decoder = match GameDecoder::new(game) {
        Ok(decoder) => decoder,
        Err(_) => return fail(statuses, ErrorCode::DecoderUnavailable),
    };
    let mut source: Box<dyn FrameSource> = match backend {
        BackendChoice::PktMon => match RealTimeFrameSource::new(game.ports()) {
            Ok(source) => Box::new(source),
            Err(_) => return fail(statuses, ErrorCode::CaptureStartFailed),
        },
        BackendChoice::Npcap => match NpcapFrameSource::new(game) {
            Ok(source) => Box::new(source),
            Err(_) => return fail(statuses, ErrorCode::CaptureStartFailed),
        },
    };
    if cancelled.is_cancelled() {
        statuses
            .state(State::Cancelled)
            .map_err(|_| LauncherRunError)?;
        return Err(LauncherRunError);
    }
    statuses.state(State::Ready).map_err(|_| LauncherRunError)?;
    statuses
        .state(State::WaitingForGame)
        .map_err(|_| LauncherRunError)?;
    let completed = match capture_complete_snapshot(
        source.as_mut(),
        &mut decoder,
        game,
        game.released_ids(),
        game.other_ids(),
        CaptureLimits {
            timeout,
            ..CaptureLimits::default()
        },
        &cancelled,
    ) {
        Ok(completed) => completed,
        Err(error) => {
            return match launcher::capture_error_code(&error) {
                Some(code) => fail(statuses, code),
                None => {
                    statuses
                        .state(State::Cancelled)
                        .map_err(|_| LauncherRunError)?;
                    Err(LauncherRunError)
                }
            };
        }
    };
    let (_, relative_path) = match write_launcher_export(&output, game, &completed, &cancelled) {
        Ok(result) => result,
        Err(OutputError::Cancelled) => {
            statuses
                .state(State::Cancelled)
                .map_err(|_| LauncherRunError)?;
            return Err(LauncherRunError);
        }
        Err(error) => return fail(statuses, launcher::output_error_code(&error)),
    };
    statuses
        .exported(completed.len(), &relative_path)
        .map_err(|_| LauncherRunError)
}
