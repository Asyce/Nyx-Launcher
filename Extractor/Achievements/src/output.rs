use crate::{Game, capture::CancelSignal, cli::OutputRoot};
use std::{
    ffi::OsStr,
    fmt,
    fs::{File, OpenOptions},
    io::Write,
    os::windows::{ffi::OsStrExt, fs::OpenOptionsExt, io::AsRawHandle},
    path::{Component, Path, PathBuf, Prefix},
    time::{Duration, SystemTime},
};
use windows::{
    Win32::{
        Foundation::HANDLE,
        Storage::FileSystem::{
            BY_HANDLE_FILE_INFORMATION, FILE_ATTRIBUTE_DIRECTORY, FILE_ATTRIBUTE_REPARSE_POINT,
            FILE_FLAG_BACKUP_SEMANTICS, FILE_FLAG_OPEN_REPARSE_POINT, FILE_LIST_DIRECTORY,
            FILE_SHARE_READ, FILE_SHARE_WRITE, FlushFileBuffers, GetDriveTypeW, GetFileAttributesW,
            GetFileInformationByHandle, INVALID_FILE_ATTRIBUTES, MOVEFILE_WRITE_THROUGH,
            MoveFileExW,
        },
        System::{Com::CoTaskMemFree, SystemInformation::GetSystemTime},
        UI::Shell::{
            FOLDERID_Downloads, FOLDERID_LocalAppData, KF_FLAG_DEFAULT, SHGetKnownFolderPath,
        },
    },
    core::PCWSTR,
};

const DRIVE_FIXED: u32 = 3;
const STALE_TEMP_AGE: Duration = Duration::from_secs(24 * 60 * 60);

#[derive(Debug)]
pub enum OutputError {
    UnsafeLocation,
    Exists,
    Cancelled,
    Io(std::io::Error),
}

impl fmt::Display for OutputError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::UnsafeLocation => {
                write!(formatter, "the protected local export folder is unsafe")
            }
            Self::Exists => write!(
                formatter,
                "an export already exists; move or delete it before trying again"
            ),
            Self::Cancelled => write!(formatter, "export cancelled"),
            Self::Io(_) => write!(
                formatter,
                "the protected local JSON could not be written safely"
            ),
        }
    }
}

impl std::error::Error for OutputError {}

impl From<std::io::Error> for OutputError {
    fn from(value: std::io::Error) -> Self {
        Self::Io(value)
    }
}

fn export_bytes_at(game: Game, ids: &[u32], exported_at: &str) -> Vec<u8> {
    let rows = ids
        .iter()
        .map(|id| format!("{{\"id\":{id},\"status\":\"complete\"}}"))
        .collect::<Vec<_>>()
        .join(",");
    format!(
        "{{\"kind\":\"pengo-achievements\",\"version\":1,\"game\":\"{}\",\"catalogVersion\":\"{}\",\"exportedAt\":\"{}\",\"achievements\":[{}]}}\n",
        game.key(),
        game.catalog_version(),
        exported_at,
        rows
    )
    .into_bytes()
}

fn wide(value: &OsStr) -> Result<Vec<u16>, OutputError> {
    let encoded = value.encode_wide().collect::<Vec<_>>();
    if encoded.contains(&0) {
        return Err(OutputError::UnsafeLocation);
    }
    Ok(encoded.into_iter().chain([0]).collect())
}

fn known_folder(id: &windows::core::GUID) -> Result<PathBuf, OutputError> {
    unsafe {
        let value = SHGetKnownFolderPath(id, KF_FLAG_DEFAULT, HANDLE(0))
            .map_err(|error| OutputError::Io(error.into()))?;
        let decoded = value.to_string();
        CoTaskMemFree(Some(value.0.cast()));
        decoded
            .map(PathBuf::from)
            .map_err(|_| OutputError::UnsafeLocation)
    }
}

fn local_app_data() -> Result<PathBuf, OutputError> {
    known_folder(&FOLDERID_LocalAppData)
}

fn downloads() -> Result<PathBuf, OutputError> {
    known_folder(&FOLDERID_Downloads)
}

fn require_absolute_local_path(path: &Path) -> Result<(), OutputError> {
    let mut components = path.components();
    match components.next() {
        Some(Component::Prefix(prefix)) if matches!(prefix.kind(), Prefix::Disk(_)) => {}
        _ => return Err(OutputError::UnsafeLocation),
    }
    if components.next() != Some(Component::RootDir)
        || components.any(|part| {
            matches!(
                part,
                Component::CurDir | Component::ParentDir | Component::Prefix(_)
            )
        })
    {
        return Err(OutputError::UnsafeLocation);
    }
    Ok(())
}

fn require_fixed_volume(path: &Path) -> Result<(), OutputError> {
    let root = path.ancestors().last().ok_or(OutputError::UnsafeLocation)?;
    if root.as_os_str().is_empty() {
        return Err(OutputError::UnsafeLocation);
    }
    let root = wide(root.as_os_str())?;
    if unsafe { GetDriveTypeW(PCWSTR(root.as_ptr())) } != DRIVE_FIXED {
        return Err(OutputError::UnsafeLocation);
    }
    Ok(())
}

fn attributes(path: &Path) -> Result<u32, OutputError> {
    let value = wide(path.as_os_str())?;
    let result = unsafe { GetFileAttributesW(PCWSTR(value.as_ptr())) };
    if result == INVALID_FILE_ATTRIBUTES {
        return Err(OutputError::Io(std::io::Error::last_os_error()));
    }
    Ok(result)
}

fn require_plain_directory(path: &Path) -> Result<(), OutputError> {
    let value = attributes(path)?;
    if value & FILE_ATTRIBUTE_DIRECTORY.0 == 0 || value & FILE_ATTRIBUTE_REPARSE_POINT.0 != 0 {
        return Err(OutputError::UnsafeLocation);
    }
    Ok(())
}

fn require_plain_ancestors(path: &Path) -> Result<(), OutputError> {
    for ancestor in path.ancestors().collect::<Vec<_>>().into_iter().rev() {
        require_plain_directory(ancestor)?;
    }
    Ok(())
}

fn ensure_plain_directory(path: &Path) -> Result<(), OutputError> {
    match std::fs::create_dir(path) {
        Ok(()) => {}
        Err(error) if error.kind() == std::io::ErrorKind::AlreadyExists => {}
        Err(error) => return Err(OutputError::Io(error)),
    }
    require_plain_directory(path)
}

fn hold_directory(path: &Path) -> Result<File, OutputError> {
    let handle = OpenOptions::new()
        .access_mode(FILE_LIST_DIRECTORY.0)
        .share_mode(FILE_SHARE_READ.0 | FILE_SHARE_WRITE.0)
        .custom_flags(FILE_FLAG_BACKUP_SEMANTICS.0 | FILE_FLAG_OPEN_REPARSE_POINT.0)
        .open(path)?;
    let mut information = BY_HANDLE_FILE_INFORMATION::default();
    let ok = unsafe {
        GetFileInformationByHandle(HANDLE(handle.as_raw_handle() as isize), &mut information)
            .as_bool()
    };
    if !ok
        || information.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY.0 == 0
        || information.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT.0 != 0
    {
        return Err(OutputError::UnsafeLocation);
    }
    Ok(handle)
}

fn secure_export_directory(base: &Path) -> Result<(PathBuf, File), OutputError> {
    require_absolute_local_path(base)?;
    require_fixed_volume(base)?;
    require_plain_ancestors(base)?;
    let pengo = base.join("Pengo");
    ensure_plain_directory(&pengo)?;
    let exports = pengo.join("Exports");
    ensure_plain_directory(&exports)?;
    let handle = hold_directory(&exports)?;
    Ok((exports, handle))
}

pub struct LauncherOutput {
    directory: PathBuf,
    relative_directory: &'static str,
    _held_directory: File,
}

pub fn prepare_launcher_output(
    root: &OutputRoot,
    game: Game,
) -> Result<LauncherOutput, OutputError> {
    let base = match root {
        OutputRoot::Downloads => {
            let downloads = downloads()?;
            require_absolute_local_path(&downloads)?;
            require_fixed_volume(&downloads)?;
            require_plain_ancestors(&downloads)?;
            let exports = downloads.join("Pengo Exports");
            ensure_plain_directory(&exports)?;
            exports
        }
        OutputRoot::Fixed(path) => {
            require_absolute_local_path(path)?;
            require_fixed_volume(path)?;
            require_plain_ancestors(path)?;
            path.clone()
        }
    };
    let game_directory = base.join(game.output_folder());
    ensure_plain_directory(&game_directory)?;
    let held = hold_directory(&game_directory)?;
    scavenge_stale_launcher_temps(&game_directory, SystemTime::now())?;
    Ok(LauncherOutput {
        directory: game_directory,
        relative_directory: game.output_folder(),
        _held_directory: held,
    })
}

fn strict_launcher_temp_name(name: &str) -> bool {
    const SUFFIX: &str = ".tmp";
    let Some(body) = name
        .strip_prefix('.')
        .and_then(|value| value.strip_suffix(SUFFIX))
    else {
        return false;
    };
    let body = body.strip_prefix("pengo-achievements-").unwrap_or(body);
    if body.len() != 23 || !body.is_ascii() {
        return false;
    }
    let bytes = body.as_bytes();
    bytes[..8].iter().all(u8::is_ascii_digit)
        && bytes[8] == b'T'
        && bytes[9..15].iter().all(u8::is_ascii_digit)
        && bytes[15] == b'Z'
        && bytes[16] == b'-'
        && bytes[17..].iter().all(u8::is_ascii_alphanumeric)
}

fn scavenge_stale_launcher_temps(directory: &Path, now: SystemTime) -> Result<(), OutputError> {
    for entry in std::fs::read_dir(directory)? {
        let entry = entry?;
        let Some(name) = entry.file_name().to_str().map(str::to_owned) else {
            continue;
        };
        if !strict_launcher_temp_name(&name) || !entry.file_type()?.is_file() {
            continue;
        }
        let modified = entry.metadata()?.modified()?;
        if now.duration_since(modified).unwrap_or_default() < STALE_TEMP_AGE {
            continue;
        }
        std::fs::remove_file(entry.path())?;
    }
    Ok(())
}

struct TempGuard(PathBuf);

impl Drop for TempGuard {
    fn drop(&mut self) {
        let _ = std::fs::remove_file(&self.0);
    }
}

fn commit_no_overwrite(from: &Path, to: &Path) -> Result<(), OutputError> {
    let from = wide(from.as_os_str())?;
    let to = wide(to.as_os_str())?;
    if unsafe {
        MoveFileExW(
            PCWSTR(from.as_ptr()),
            PCWSTR(to.as_ptr()),
            MOVEFILE_WRITE_THROUGH,
        )
        .as_bool()
    } {
        return Ok(());
    }
    let error = std::io::Error::last_os_error();
    Err(if error.kind() == std::io::ErrorKind::AlreadyExists {
        OutputError::Exists
    } else {
        OutputError::Io(error)
    })
}

fn write_export_at(base: &Path, game: Game, ids: &[u32]) -> Result<PathBuf, OutputError> {
    let (directory, _held_directory) = secure_export_directory(base)?;
    let destination = directory.join(format!("pengo-achievements-{}.json", game.key()));
    match std::fs::symlink_metadata(&destination) {
        Ok(_) => return Err(OutputError::Exists),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
        Err(error) => return Err(OutputError::Io(error)),
    }

    let temporary = tempfile::Builder::new()
        .prefix(".pengo-achievements-")
        .suffix(".tmp")
        .tempfile_in(&directory)?;
    let mut temporary = temporary;
    let timestamp = utc_timestamp();
    temporary.write_all(&export_bytes_at(game, ids, &timestamp.iso))?;
    temporary.flush()?;
    if !unsafe { FlushFileBuffers(HANDLE(temporary.as_file().as_raw_handle() as isize)).as_bool() }
    {
        return Err(OutputError::Io(std::io::Error::last_os_error()));
    }
    let (_file, temporary_path) = temporary
        .keep()
        .map_err(|error| OutputError::Io(error.error))?;
    drop(_file);
    let mut cleanup = TempGuard(temporary_path);
    commit_no_overwrite(&cleanup.0, &destination)?;
    cleanup.0 = PathBuf::new();
    Ok(destination)
}

struct UtcTimestamp {
    compact: String,
    iso: String,
}

fn utc_timestamp() -> UtcTimestamp {
    let value = unsafe { GetSystemTime() };
    UtcTimestamp {
        compact: format!(
            "{:04}{:02}{:02}T{:02}{:02}{:02}Z",
            value.wYear, value.wMonth, value.wDay, value.wHour, value.wMinute, value.wSecond
        ),
        iso: format!(
            "{:04}-{:02}-{:02}T{:02}:{:02}:{:02}Z",
            value.wYear, value.wMonth, value.wDay, value.wHour, value.wMinute, value.wSecond
        ),
    }
}

pub fn write_launcher_export(
    prepared: &LauncherOutput,
    game: Game,
    ids: &[u32],
    cancelled: &dyn CancelSignal,
) -> Result<(PathBuf, String), OutputError> {
    if cancelled.is_cancelled() {
        return Err(OutputError::Cancelled);
    }
    let timestamp = utc_timestamp();
    let prefix = format!(".{}-", timestamp.compact);
    let mut temporary = tempfile::Builder::new()
        .prefix(&prefix)
        .suffix(".tmp")
        .tempfile_in(&prepared.directory)?;
    temporary.write_all(&export_bytes_at(game, ids, &timestamp.iso))?;
    temporary.flush()?;
    if !unsafe { FlushFileBuffers(HANDLE(temporary.as_file().as_raw_handle() as isize)).as_bool() }
    {
        return Err(OutputError::Io(std::io::Error::last_os_error()));
    }
    if cancelled.is_cancelled() {
        return Err(OutputError::Cancelled);
    }
    let temporary_name = temporary
        .path()
        .file_name()
        .and_then(OsStr::to_str)
        .ok_or(OutputError::UnsafeLocation)?;
    let stem = temporary_name
        .strip_prefix('.')
        .and_then(|value| value.strip_suffix(".tmp"))
        .ok_or(OutputError::UnsafeLocation)?;
    let final_name = format!("{stem}.json");
    let destination = prepared.directory.join(&final_name);
    let (_file, temporary_path) = temporary
        .keep()
        .map_err(|error| OutputError::Io(error.error))?;
    drop(_file);
    let mut cleanup = TempGuard(temporary_path);
    if cancelled.is_cancelled() {
        return Err(OutputError::Cancelled);
    }
    commit_no_overwrite(&cleanup.0, &destination)?;
    cleanup.0 = PathBuf::new();
    let relative = format!("{}/{}", prepared.relative_directory, final_name);
    Ok((destination, relative))
}

pub fn write_export(game: Game, ids: &[u32]) -> Result<PathBuf, OutputError> {
    write_export_at(&local_app_data()?, game, ids)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};

    struct CancelOnSecondCheck(AtomicUsize);

    impl CancelSignal for CancelOnSecondCheck {
        fn is_cancelled(&self) -> bool {
            self.0.fetch_add(1, Ordering::SeqCst) >= 1
        }
    }

    #[test]
    fn exact_json_is_bomless() {
        assert_eq!(
            export_bytes_at(Game::Gi, &[10, 20], "2026-07-26T12:00:00Z"),
            b"{\"kind\":\"pengo-achievements\",\"version\":1,\"game\":\"gi\",\"catalogVersion\":\"gi-6.7\",\"exportedAt\":\"2026-07-26T12:00:00Z\",\"achievements\":[{\"id\":10,\"status\":\"complete\"},{\"id\":20,\"status\":\"complete\"}]}\n"
        );
        assert_eq!(
            export_bytes_at(Game::Hsr, &[30, 40], "2026-07-26T12:00:00Z"),
            b"{\"kind\":\"pengo-achievements\",\"version\":1,\"game\":\"hsr\",\"catalogVersion\":\"hsr-4.4\",\"exportedAt\":\"2026-07-26T12:00:00Z\",\"achievements\":[{\"id\":30,\"status\":\"complete\"},{\"id\":40,\"status\":\"complete\"}]}\n"
        );
    }

    #[test]
    fn fixed_local_export_refuses_overwrite_and_leaves_no_temp() {
        let base = tempfile::tempdir().unwrap();
        let output = write_export_at(base.path(), Game::Gi, &[20]).unwrap();
        let value: serde_json::Value =
            serde_json::from_slice(&std::fs::read(&output).unwrap()).unwrap();
        assert_eq!(value["kind"], "pengo-achievements");
        assert_eq!(value["game"], "gi");
        assert_eq!(value["achievements"][0]["id"], 20);
        assert!(matches!(
            write_export_at(base.path(), Game::Gi, &[30]),
            Err(OutputError::Exists)
        ));
        let entries = std::fs::read_dir(output.parent().unwrap())
            .unwrap()
            .collect::<Result<Vec<_>, _>>()
            .unwrap();
        assert_eq!(entries.len(), 1);
    }

    #[test]
    fn rejects_non_fixed_and_reparse_locations() {
        assert!(matches!(
            require_fixed_volume(Path::new(r"\\server\share")),
            Err(OutputError::UnsafeLocation)
        ));
        let container = tempfile::tempdir().unwrap();
        let real = container.path().join("real");
        let linked = container.path().join("linked");
        std::fs::create_dir(&real).unwrap();
        if std::os::windows::fs::symlink_dir(&real, &linked).is_ok() {
            assert!(matches!(
                secure_export_directory(&linked),
                Err(OutputError::UnsafeLocation)
            ));
        }
    }

    #[test]
    fn abandoned_temporary_file_is_cleaned() {
        let directory = tempfile::tempdir().unwrap();
        let path = directory.path().join("orphan.tmp");
        std::fs::write(&path, b"not an export").unwrap();
        drop(TempGuard(path.clone()));
        assert!(!path.exists());
    }

    #[test]
    fn startup_scavenges_only_strictly_named_stale_temporary_files() {
        let directory = tempfile::tempdir().unwrap();
        let stale = directory
            .path()
            .join(".pengo-achievements-20260717T120000Z-abc123.tmp");
        let fresh = directory
            .path()
            .join(".pengo-achievements-20260717T120001Z-def456.tmp");
        let unrelated = directory.path().join("notes.tmp");
        let almost = directory
            .path()
            .join(".pengo-achievements-20260717T120000Z-abc12!.tmp");
        for path in [&stale, &fresh, &unrelated, &almost] {
            std::fs::write(path, b"preserve unless strictly stale").unwrap();
        }
        let now = SystemTime::now();
        std::fs::File::options()
            .write(true)
            .open(&stale)
            .unwrap()
            .set_times(
                std::fs::FileTimes::new()
                    .set_modified(now - STALE_TEMP_AGE - Duration::from_secs(1)),
            )
            .unwrap();

        scavenge_stale_launcher_temps(directory.path(), now).unwrap();

        assert!(!stale.exists());
        assert!(fresh.exists());
        assert!(unrelated.exists());
        assert!(almost.exists());
    }

    #[test]
    fn launcher_output_is_unique_atomic_and_exact() {
        let root = tempfile::tempdir().unwrap();
        let prepared =
            prepare_launcher_output(&OutputRoot::Fixed(root.path().to_path_buf()), Game::Hsr)
                .unwrap();
        let cancel = AtomicBool::new(false);
        let (first, first_relative) =
            write_launcher_export(&prepared, Game::Hsr, &[30, 40], &cancel).unwrap();
        let (second, second_relative) =
            write_launcher_export(&prepared, Game::Hsr, &[50], &cancel).unwrap();
        assert_ne!(first, second);
        assert_ne!(first_relative, second_relative);
        assert!(first_relative.starts_with("Honkai Star Rail/"));
        for relative in [&first_relative, &second_relative] {
            let file_name = Path::new(relative)
                .file_name()
                .and_then(OsStr::to_str)
                .unwrap();
            let stem = file_name.strip_suffix(".json").unwrap();
            assert!(strict_launcher_temp_name(&format!(".{stem}.tmp")));
        }
        let first_value: serde_json::Value =
            serde_json::from_slice(&std::fs::read(first).unwrap()).unwrap();
        let second_value: serde_json::Value =
            serde_json::from_slice(&std::fs::read(second).unwrap()).unwrap();
        assert_eq!(first_value["catalogVersion"], "hsr-4.4");
        assert_eq!(first_value["achievements"].as_array().unwrap().len(), 2);
        assert_eq!(second_value["achievements"][0]["id"], 50);
        assert!(
            std::fs::read_dir(root.path().join("Honkai Star Rail"))
                .unwrap()
                .all(|entry| !entry
                    .unwrap()
                    .file_name()
                    .to_string_lossy()
                    .ends_with(".tmp"))
        );
    }

    #[test]
    fn launcher_cancel_leaves_no_output_or_temporary_file() {
        let root = tempfile::tempdir().unwrap();
        let prepared =
            prepare_launcher_output(&OutputRoot::Fixed(root.path().to_path_buf()), Game::Gi)
                .unwrap();
        let result = write_launcher_export(
            &prepared,
            Game::Gi,
            &[20],
            &CancelOnSecondCheck(AtomicUsize::new(0)),
        );
        assert!(matches!(result, Err(OutputError::Cancelled)));
        assert_eq!(
            std::fs::read_dir(root.path().join("Genshin Impact"))
                .unwrap()
                .count(),
            0
        );
    }

    #[test]
    fn atomic_commit_never_replaces_an_existing_file() {
        let directory = tempfile::tempdir().unwrap();
        let source = directory.path().join("source.tmp");
        let destination = directory.path().join("destination.json");
        std::fs::write(&source, b"new secret data").unwrap();
        std::fs::write(&destination, b"preserved user data").unwrap();
        assert!(matches!(
            commit_no_overwrite(&source, &destination),
            Err(OutputError::Exists)
        ));
        assert_eq!(std::fs::read(&destination).unwrap(), b"preserved user data");
        assert_eq!(std::fs::read(&source).unwrap(), b"new secret data");
    }

    #[test]
    fn fixed_root_rejects_relative_parent_unc_and_reparse_paths() {
        for path in [
            PathBuf::from("relative"),
            PathBuf::from(r"C:\safe\..\escape"),
            PathBuf::from(r"\\server\share"),
            PathBuf::from(r"\\?\C:\device"),
        ] {
            assert!(matches!(
                prepare_launcher_output(&OutputRoot::Fixed(path), Game::Gi),
                Err(OutputError::UnsafeLocation)
            ));
        }
    }
}
