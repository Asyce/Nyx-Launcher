use std::sync::atomic::{AtomicBool, Ordering};
use windows::Win32::System::LibraryLoader::{
    LOAD_LIBRARY_SEARCH_SYSTEM32, SetDefaultDllDirectories,
};

static LAUNCHER_MODE: AtomicBool = AtomicBool::new(false);

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct DllSearchError;

/// Restrict all later DLL dependency lookup to Windows' trusted System32 folder.
pub fn harden_process_dll_search() -> Result<(), DllSearchError> {
    if unsafe { SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32) }.as_bool() {
        Ok(())
    } else {
        Err(DllSearchError)
    }
}

/// Do not reveal panic payloads, source paths, or backtraces to the console.
pub fn install_safe_panic_hook() {
    std::panic::set_hook(Box::new(|_| {
        if !LAUNCHER_MODE.load(Ordering::SeqCst) {
            eprintln!("Error: the extractor stopped safely; no diagnostic details were written.");
        }
    }));
}

pub fn set_launcher_mode() {
    LAUNCHER_MODE.store(true, Ordering::SeqCst);
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::{
        ffi::{OsStr, OsString},
        io::Write,
        os::windows::ffi::{OsStrExt, OsStringExt},
        path::{Path, PathBuf},
    };
    use windows::{
        Win32::{
            Foundation::HMODULE,
            System::{
                LibraryLoader::{FreeLibrary, GetModuleFileNameW, LoadLibraryW},
                SystemInformation::GetSystemDirectoryW,
            },
        },
        core::PCWSTR,
    };

    struct Decoys(Vec<PathBuf>);

    impl Drop for Decoys {
        fn drop(&mut self) {
            for path in &self.0 {
                let _ = std::fs::remove_file(path);
            }
        }
    }

    struct Module(HMODULE);

    impl Drop for Module {
        fn drop(&mut self) {
            unsafe { FreeLibrary(self.0) };
        }
    }

    fn module_path(module: HMODULE) -> PathBuf {
        let mut buffer = vec![0_u16; 32_768];
        let length = unsafe { GetModuleFileNameW(module, &mut buffer) } as usize;
        assert!(length > 0 && length < buffer.len());
        PathBuf::from(OsString::from_wide(&buffer[..length]))
    }

    fn system32() -> PathBuf {
        let mut buffer = vec![0_u16; 32_768];
        let length = unsafe { GetSystemDirectoryW(Some(&mut buffer)) } as usize;
        assert!(length > 0 && length < buffer.len());
        PathBuf::from(OsString::from_wide(&buffer[..length]))
    }

    fn same_path(left: &Path, right: &Path) -> bool {
        left.as_os_str()
            .to_string_lossy()
            .eq_ignore_ascii_case(&right.as_os_str().to_string_lossy())
    }

    #[test]
    fn global_dll_policy_ignores_sibling_dbghelp_and_pktmon_decoys() {
        harden_process_dll_search().unwrap();
        let sibling_dir = std::env::current_exe()
            .unwrap()
            .parent()
            .unwrap()
            .to_path_buf();
        let decoy_paths = ["dbghelp.dll", "PktMonApi.dll"].map(|name| sibling_dir.join(name));
        for path in &decoy_paths {
            let mut file = std::fs::OpenOptions::new()
                .write(true)
                .create_new(true)
                .open(path)
                .unwrap();
            file.write_all(b"Pengo test decoy; not a DLL").unwrap();
        }
        let _decoys = Decoys(decoy_paths.to_vec());

        for name in ["dbghelp.dll", "PktMonApi.dll"] {
            let wide = OsStr::new(name)
                .encode_wide()
                .chain([0])
                .collect::<Vec<_>>();
            let module = Module(unsafe { LoadLibraryW(PCWSTR(wide.as_ptr())) }.unwrap());
            assert!(same_path(
                module_path(module.0).parent().unwrap(),
                &system32()
            ));
        }
    }
}
