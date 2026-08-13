use crate::{
    Game,
    capture::{FrameEvent, FrameSource},
};
use sha2::{Digest, Sha256};
use std::{
    ffi::{CStr, CString, OsString, c_char, c_int, c_uint},
    fs::File,
    io::Read,
    mem::transmute,
    os::windows::ffi::{OsStrExt, OsStringExt},
    path::{Path, PathBuf},
    ptr::NonNull,
    thread,
    time::{Duration, Instant},
};
use windows::{
    Win32::{
        Foundation::{ERROR_SUCCESS, HMODULE},
        NetworkManagement::{
            IpHelper::{ConvertInterfaceIndexToLuid, ConvertInterfaceLuidToGuid, GetBestInterface},
            Ndis::NET_LUID_LH,
        },
        Security::SC_HANDLE,
        System::{
            LibraryLoader::{
                FreeLibrary, GetModuleFileNameW, GetModuleHandleW, GetProcAddress,
                LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR, LOAD_LIBRARY_SEARCH_SYSTEM32, LoadLibraryExW,
            },
            Registry::{
                HKEY_LOCAL_MACHINE, RRF_NOEXPAND, RRF_RT_REG_DWORD, RRF_RT_REG_EXPAND_SZ,
                RegGetValueW,
            },
            Services::{
                CloseServiceHandle, OpenSCManagerW, OpenServiceW, QueryServiceStatusEx,
                SC_MANAGER_CONNECT, SC_STATUS_PROCESS_INFO, SERVICE_QUERY_STATUS, SERVICE_RUNNING,
                SERVICE_STATUS_PROCESS, SERVICE_SYSTEM_START,
            },
            SystemInformation::GetSystemDirectoryW,
        },
        UI::Shell::IsUserAnAdmin,
    },
    core::{GUID, PCWSTR},
    s, w,
};

pub const GI_FILTER: &str = "udp and (port 22101 or port 22102)";
pub const HSR_FILTER: &str = "udp and (port 23301 or port 23302)";

pub const fn filter_for_game(game: Game) -> &'static str {
    match game {
        Game::Gi => GI_FILTER,
        Game::Hsr => HSR_FILTER,
    }
}
pub const SNAPLEN: c_int = 9_000;
pub const KERNEL_BUFFER_BYTES: c_int = 1024 * 1024;
const DLT_EN10MB: c_int = 1;
const PCAP_NETMASK_UNKNOWN: c_uint = 0xffff_ffff;
const POLL_INTERVAL: Duration = Duration::from_millis(10);
const ERRBUF_SIZE: usize = 256;

const APPROVED_VERSION: &str =
    "Npcap version 1.88, based on libpcap version 1.10.6 (64-bit time_t)";
const APPROVED_WPCAP_SHA256: &str =
    "D1CA7FCF9128D02A75EAF29CE9A9D85C5697377460F92420D976DA187521CF39";
const APPROVED_PACKET_SHA256: &str =
    "2793CE72F0E04D5885AAEE1273A7373441D01934B2CFF3886B031C13CA826345";
const APPROVED_DRIVER_SHA256: &str =
    "13D598E277E9C7BF43688D7087EF9B944E8036561A1E7169D31D9EC1D38F9A01";

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
struct ApprovedSettings {
    admin_only: u32,
    winpcap_compatible: u32,
    loopback_support: u32,
    dlt_null: u32,
    dot11_support: u32,
}

const APPROVED_SETTINGS: ApprovedSettings = ApprovedSettings {
    admin_only: 0,
    winpcap_compatible: 1,
    loopback_support: 1,
    dlt_null: 1,
    dot11_support: 0,
};

#[repr(C)]
struct Pcap {
    _private: [u8; 0],
}

#[repr(C)]
struct BpfInsn {
    code: u16,
    jt: u8,
    jf: u8,
    k: u32,
}

#[repr(C)]
struct BpfProgram {
    length: c_uint,
    instructions: *mut BpfInsn,
}

impl Default for BpfProgram {
    fn default() -> Self {
        Self {
            length: 0,
            instructions: std::ptr::null_mut(),
        }
    }
}

#[repr(C)]
struct PcapTimeval {
    seconds: i32,
    microseconds: i32,
}

#[repr(C)]
struct PcapHeader {
    timestamp: PcapTimeval,
    captured_length: c_uint,
    original_length: c_uint,
}

type PcapLibVersion = unsafe extern "C" fn() -> *const c_char;
type PcapCreate = unsafe extern "C" fn(*const c_char, *mut c_char) -> *mut Pcap;
type PcapSetInt = unsafe extern "C" fn(*mut Pcap, c_int) -> c_int;
type PcapActivate = unsafe extern "C" fn(*mut Pcap) -> c_int;
type PcapDatalink = unsafe extern "C" fn(*mut Pcap) -> c_int;
type PcapCompile =
    unsafe extern "C" fn(*mut Pcap, *mut BpfProgram, *const c_char, c_int, c_uint) -> c_int;
type PcapSetFilter = unsafe extern "C" fn(*mut Pcap, *mut BpfProgram) -> c_int;
type PcapFreeCode = unsafe extern "C" fn(*mut BpfProgram);
type PcapSetNonBlock = unsafe extern "C" fn(*mut Pcap, c_int, *mut c_char) -> c_int;
type PcapNextEx = unsafe extern "C" fn(*mut Pcap, *mut *mut PcapHeader, *mut *const u8) -> c_int;
type PcapClose = unsafe extern "C" fn(*mut Pcap);
#[cfg(test)]
type PcapOpenDead = unsafe extern "C" fn(c_int, c_int) -> *mut Pcap;
#[cfg(test)]
type PcapOfflineFilter =
    unsafe extern "C" fn(*const BpfProgram, *const PcapHeader, *const u8) -> c_int;

struct PcapApi {
    module: HMODULE,
    create: PcapCreate,
    set_snaplen: PcapSetInt,
    set_promisc: PcapSetInt,
    set_timeout: PcapSetInt,
    set_buffer_size: PcapSetInt,
    set_immediate_mode: PcapSetInt,
    activate: PcapActivate,
    datalink: PcapDatalink,
    compile: PcapCompile,
    set_filter: PcapSetFilter,
    free_code: PcapFreeCode,
    set_nonblock: PcapSetNonBlock,
    next_ex: PcapNextEx,
    close: PcapClose,
    #[cfg(test)]
    open_dead: PcapOpenDead,
    #[cfg(test)]
    offline_filter: PcapOfflineFilter,
}

impl PcapApi {
    fn load() -> Result<Self, String> {
        if preloaded_module(w!("wpcap.dll")) || preloaded_module(w!("packet.dll")) {
            return Err("Npcap was already loaded before Pengo could verify it".into());
        }
        let paths = approved_paths()?;
        verify_installation(&paths)?;
        let wide = wide_path(&paths.wpcap);
        let module = unsafe {
            LoadLibraryExW(
                PCWSTR(wide.as_ptr()),
                None,
                LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32,
            )
        }
        .map_err(|_| "the reviewed Npcap library could not be loaded")?;
        let mut module_guard = ModuleGuard(Some(module));
        if !same_path(&loaded_module_path(module)?, &paths.wpcap) {
            return Err("Npcap loaded from an unexpected folder".into());
        }
        let packet = unsafe { GetModuleHandleW(w!("packet.dll")) }
            .map_err(|_| "Npcap did not load its reviewed Packet library")?;
        if !same_path(&loaded_module_path(packet)?, &paths.packet) {
            return Err("Npcap loaded an unexpected Packet library".into());
        }

        macro_rules! proc {
            ($name:literal, $kind:ty) => {{
                let address = unsafe { GetProcAddress(module, s!($name)) }
                    .ok_or("the reviewed Npcap API is incomplete")?;
                unsafe { transmute::<unsafe extern "system" fn() -> isize, $kind>(address) }
            }};
        }

        let lib_version = proc!("pcap_lib_version", PcapLibVersion);
        let version_pointer = unsafe { lib_version() };
        if version_pointer.is_null()
            || !reported_version_is_approved(unsafe { CStr::from_ptr(version_pointer) }.to_bytes())
        {
            return Err("Npcap's reported version does not match the reviewed build".into());
        }
        let create = proc!("pcap_create", PcapCreate);
        let set_snaplen = proc!("pcap_set_snaplen", PcapSetInt);
        let set_promisc = proc!("pcap_set_promisc", PcapSetInt);
        let set_timeout = proc!("pcap_set_timeout", PcapSetInt);
        let set_buffer_size = proc!("pcap_set_buffer_size", PcapSetInt);
        let set_immediate_mode = proc!("pcap_set_immediate_mode", PcapSetInt);
        let activate = proc!("pcap_activate", PcapActivate);
        let datalink = proc!("pcap_datalink", PcapDatalink);
        let compile = proc!("pcap_compile", PcapCompile);
        let set_filter = proc!("pcap_setfilter", PcapSetFilter);
        let free_code = proc!("pcap_freecode", PcapFreeCode);
        let set_nonblock = proc!("pcap_setnonblock", PcapSetNonBlock);
        let next_ex = proc!("pcap_next_ex", PcapNextEx);
        let close = proc!("pcap_close", PcapClose);
        #[cfg(test)]
        let open_dead = proc!("pcap_open_dead", PcapOpenDead);
        #[cfg(test)]
        let offline_filter = proc!("pcap_offline_filter", PcapOfflineFilter);
        let module = module_guard
            .0
            .take()
            .ok_or("Npcap module ownership failed")?;
        Ok(Self {
            module,
            create,
            set_snaplen,
            set_promisc,
            set_timeout,
            set_buffer_size,
            set_immediate_mode,
            activate,
            datalink,
            compile,
            set_filter,
            free_code,
            set_nonblock,
            next_ex,
            close,
            #[cfg(test)]
            open_dead,
            #[cfg(test)]
            offline_filter,
        })
    }

    fn unload(mut self) -> Result<(), String> {
        if unsafe { FreeLibrary(self.module) }.as_bool() {
            self.module = HMODULE(0);
            Ok(())
        } else {
            Err("Npcap could not be unloaded cleanly".into())
        }
    }
}

impl Drop for PcapApi {
    fn drop(&mut self) {
        if self.module.0 != 0 {
            unsafe {
                FreeLibrary(self.module);
            }
        }
    }
}

struct ModuleGuard(Option<HMODULE>);

impl Drop for ModuleGuard {
    fn drop(&mut self) {
        if let Some(module) = self.0.take() {
            unsafe {
                FreeLibrary(module);
            }
        }
    }
}

trait CaptureCloser {
    fn close_capture(&self, capture: *mut Pcap);
}

trait CaptureSetupApi: CaptureCloser {
    fn create_capture(&self, device: *const c_char, error: *mut c_char) -> *mut Pcap;
    fn set_snaplen(&self, capture: *mut Pcap, value: c_int) -> c_int;
    fn set_promisc(&self, capture: *mut Pcap, value: c_int) -> c_int;
    fn set_timeout(&self, capture: *mut Pcap, value: c_int) -> c_int;
    fn set_buffer_size(&self, capture: *mut Pcap, value: c_int) -> c_int;
    fn set_immediate_mode(&self, capture: *mut Pcap, value: c_int) -> c_int;
    fn activate(&self, capture: *mut Pcap) -> c_int;
    fn datalink(&self, capture: *mut Pcap) -> c_int;
    fn compile_filter(
        &self,
        capture: *mut Pcap,
        program: *mut BpfProgram,
        filter: *const c_char,
        optimize: c_int,
        netmask: c_uint,
    ) -> c_int;
    fn set_filter(&self, capture: *mut Pcap, program: *mut BpfProgram) -> c_int;
    fn free_filter(&self, program: *mut BpfProgram);
    fn set_nonblock(&self, capture: *mut Pcap, value: c_int, error: *mut c_char) -> c_int;
}

impl CaptureCloser for PcapApi {
    fn close_capture(&self, capture: *mut Pcap) {
        unsafe { (self.close)(capture) }
    }
}

impl CaptureSetupApi for PcapApi {
    fn create_capture(&self, device: *const c_char, error: *mut c_char) -> *mut Pcap {
        unsafe { (self.create)(device, error) }
    }

    fn set_snaplen(&self, capture: *mut Pcap, value: c_int) -> c_int {
        unsafe { (self.set_snaplen)(capture, value) }
    }

    fn set_promisc(&self, capture: *mut Pcap, value: c_int) -> c_int {
        unsafe { (self.set_promisc)(capture, value) }
    }

    fn set_timeout(&self, capture: *mut Pcap, value: c_int) -> c_int {
        unsafe { (self.set_timeout)(capture, value) }
    }

    fn set_buffer_size(&self, capture: *mut Pcap, value: c_int) -> c_int {
        unsafe { (self.set_buffer_size)(capture, value) }
    }

    fn set_immediate_mode(&self, capture: *mut Pcap, value: c_int) -> c_int {
        unsafe { (self.set_immediate_mode)(capture, value) }
    }

    fn activate(&self, capture: *mut Pcap) -> c_int {
        unsafe { (self.activate)(capture) }
    }

    fn datalink(&self, capture: *mut Pcap) -> c_int {
        unsafe { (self.datalink)(capture) }
    }

    fn compile_filter(
        &self,
        capture: *mut Pcap,
        program: *mut BpfProgram,
        filter: *const c_char,
        optimize: c_int,
        netmask: c_uint,
    ) -> c_int {
        unsafe { (self.compile)(capture, program, filter, optimize, netmask) }
    }

    fn set_filter(&self, capture: *mut Pcap, program: *mut BpfProgram) -> c_int {
        unsafe { (self.set_filter)(capture, program) }
    }

    fn free_filter(&self, program: *mut BpfProgram) {
        unsafe { (self.free_code)(program) }
    }

    fn set_nonblock(&self, capture: *mut Pcap, value: c_int, error: *mut c_char) -> c_int {
        unsafe { (self.set_nonblock)(capture, value, error) }
    }
}

struct CaptureConstructionGuard<'a, C: CaptureCloser + ?Sized> {
    closer: &'a C,
    capture: Option<NonNull<Pcap>>,
}

impl<'a, C: CaptureCloser + ?Sized> CaptureConstructionGuard<'a, C> {
    fn new(closer: &'a C, capture: NonNull<Pcap>) -> Self {
        Self {
            closer,
            capture: Some(capture),
        }
    }

    fn disarm(mut self) -> NonNull<Pcap> {
        self.capture.take().expect("capture guard must be armed")
    }
}

impl<C: CaptureCloser + ?Sized> Drop for CaptureConstructionGuard<'_, C> {
    fn drop(&mut self) {
        if let Some(capture) = self.capture.take() {
            self.closer.close_capture(capture.as_ptr());
        }
    }
}

struct FilterProgramGuard<'a, A: CaptureSetupApi + ?Sized> {
    api: &'a A,
    program: BpfProgram,
}

impl<A: CaptureSetupApi + ?Sized> Drop for FilterProgramGuard<'_, A> {
    fn drop(&mut self) {
        self.api.free_filter(&mut self.program)
    }
}

fn configure_capture<A: CaptureSetupApi + ?Sized>(
    api: &A,
    device: &CStr,
    filter: &CStr,
) -> Result<NonNull<Pcap>, String> {
    let mut error_buffer = [0 as c_char; ERRBUF_SIZE];
    let capture = NonNull::new(api.create_capture(device.as_ptr(), error_buffer.as_mut_ptr()))
        .ok_or("Npcap could not open the default network adapter")?;
    let construction = CaptureConstructionGuard::new(api, capture);
    let handle = capture.as_ptr();
    macro_rules! require_setter {
        ($call:expr, $label:literal) => {
            if $call != 0 {
                return Err(concat!("Npcap rejected the ", $label).into());
            }
        };
    }
    require_setter!(api.set_snaplen(handle, SNAPLEN), "snapshot limit");
    require_setter!(api.set_promisc(handle, 0), "non-promiscuous mode");
    require_setter!(api.set_timeout(handle, 1), "read timeout");
    require_setter!(
        api.set_buffer_size(handle, KERNEL_BUFFER_BYTES),
        "kernel buffer limit"
    );
    require_setter!(api.set_immediate_mode(handle, 1), "immediate mode");
    if api.activate(handle) != 0 {
        return Err("Npcap could not activate the default network adapter".into());
    }
    if api.datalink(handle) != DLT_EN10MB {
        return Err("the default network adapter does not provide Ethernet frames".into());
    }
    let mut program = BpfProgram::default();
    if api.compile_filter(
        handle,
        &mut program,
        filter.as_ptr(),
        1,
        PCAP_NETMASK_UNKNOWN,
    ) != 0
    {
        return Err("Npcap could not compile Pengo's fixed port filter".into());
    }
    let mut filter_guard = FilterProgramGuard { api, program };
    if api.set_filter(handle, &mut filter_guard.program) != 0 {
        return Err("Npcap could not apply Pengo's fixed port filter".into());
    }
    if api.set_nonblock(handle, 1, error_buffer.as_mut_ptr()) != 0 {
        return Err("Npcap could not enable bounded nonblocking reads".into());
    }
    Ok(construction.disarm())
}

pub struct NpcapFrameSource {
    api: Option<PcapApi>,
    capture: Option<NonNull<Pcap>>,
    device: CString,
    filter: CString,
    started: bool,
}

impl NpcapFrameSource {
    pub fn new(game: Game) -> Result<Self, String> {
        if unsafe { IsUserAnAdmin().as_bool() } {
            return Err(
                "Npcap capture refuses Administrator mode; reopen PowerShell normally".into(),
            );
        }
        let api = PcapApi::load()?;
        let device = CString::new(default_route_device()?)
            .map_err(|_| "the default network adapter name was invalid")?;
        let filter =
            CString::new(filter_for_game(game)).expect("the fixed game filters have no NUL byte");
        Ok(Self {
            api: Some(api),
            capture: None,
            device,
            filter,
            started: false,
        })
    }
}

impl FrameSource for NpcapFrameSource {
    fn start(&mut self) -> Result<(), String> {
        if self.started || self.capture.is_some() {
            return Err("Npcap capture was already started".into());
        }
        let api = self.api.as_ref().ok_or("Npcap was already unloaded")?;
        self.capture = Some(configure_capture(api, &self.device, &self.filter)?);
        self.started = true;
        Ok(())
    }

    fn next(&mut self, wait: Duration) -> Result<FrameEvent, String> {
        if !self.started {
            return Err("Npcap capture is not running".into());
        }
        let api = self.api.as_ref().ok_or("Npcap was unloaded")?;
        let handle = self.capture.ok_or("Npcap capture handle is missing")?;
        let deadline = Instant::now()
            .checked_add(wait)
            .unwrap_or_else(Instant::now);
        loop {
            let mut header = std::ptr::null_mut();
            let mut data = std::ptr::null();
            match unsafe { (api.next_ex)(handle.as_ptr(), &mut header, &mut data) } {
                1 => {
                    if header.is_null() {
                        return Err("Npcap returned a packet without a header".into());
                    }
                    let header = unsafe { &*header };
                    let Some(captured_length) = checked_capture_length(
                        header.captured_length,
                        header.original_length,
                        data.is_null(),
                    ) else {
                        return Err("Npcap returned a packet outside Pengo's bounds".into());
                    };
                    let frame = if captured_length == 0 {
                        Vec::new()
                    } else {
                        unsafe { std::slice::from_raw_parts(data, captured_length) }.to_vec()
                    };
                    return Ok(FrameEvent::Frame(frame));
                }
                0 => {
                    let now = Instant::now();
                    if now >= deadline {
                        return Ok(FrameEvent::Idle);
                    }
                    thread::sleep(POLL_INTERVAL.min(deadline.saturating_duration_since(now)));
                }
                -2 => return Ok(FrameEvent::Closed),
                _ => return Err("Npcap stopped while reading the bounded capture".into()),
            }
        }
    }

    fn stop(&mut self) -> Result<(), String> {
        self.started = false;
        Ok(())
    }

    fn unload(&mut self) -> Result<(), String> {
        self.started = false;
        if let (Some(api), Some(capture)) = (self.api.as_ref(), self.capture.take()) {
            api.close_capture(capture.as_ptr());
        }
        self.api.take().map_or(Ok(()), PcapApi::unload)
    }
}

impl Drop for NpcapFrameSource {
    fn drop(&mut self) {
        self.started = false;
        if let (Some(api), Some(capture)) = (self.api.as_ref(), self.capture.take()) {
            api.close_capture(capture.as_ptr());
        }
    }
}

struct ApprovedPaths {
    wpcap: PathBuf,
    packet: PathBuf,
    driver: PathBuf,
}

fn approved_paths() -> Result<ApprovedPaths, String> {
    let system32 = system32_path()?;
    Ok(ApprovedPaths {
        wpcap: system32.join("Npcap").join("wpcap.dll"),
        packet: system32.join("Npcap").join("Packet.dll"),
        driver: system32.join("drivers").join("npcap.sys"),
    })
}

fn verify_installation(paths: &ApprovedPaths) -> Result<(), String> {
    let settings = installed_settings()?;
    let service = installed_service()?;
    let approved = installation_files_and_settings_are_approved(
        &sha256(&paths.wpcap)?,
        &sha256(&paths.packet)?,
        &sha256(&paths.driver)?,
        settings,
    ) && service_is_approved(&service);
    approved
        .then_some(())
        .ok_or_else(|| "Npcap does not exactly match Pengo's reviewed 1.88 installation".into())
}

fn installation_files_and_settings_are_approved(
    wpcap_hash: &str,
    packet_hash: &str,
    driver_hash: &str,
    settings: ApprovedSettings,
) -> bool {
    wpcap_hash.eq_ignore_ascii_case(APPROVED_WPCAP_SHA256)
        && packet_hash.eq_ignore_ascii_case(APPROVED_PACKET_SHA256)
        && driver_hash.eq_ignore_ascii_case(APPROVED_DRIVER_SHA256)
        && settings == APPROVED_SETTINGS
}

fn reported_version_is_approved(version: &[u8]) -> bool {
    version == APPROVED_VERSION.as_bytes()
}

#[derive(Clone, Debug, Eq, PartialEq)]
struct InstalledService {
    running: bool,
    start_type: u32,
    image_path: String,
}

const APPROVED_SERVICE_IMAGE: &str = r"\SystemRoot\system32\DRIVERS\npcap.sys";

fn service_is_approved(service: &InstalledService) -> bool {
    service.running
        && service.start_type == SERVICE_SYSTEM_START.0
        && service
            .image_path
            .eq_ignore_ascii_case(APPROVED_SERVICE_IMAGE)
}

fn installed_service() -> Result<InstalledService, String> {
    let manager = ServiceHandle(
        unsafe { OpenSCManagerW(None, None, SC_MANAGER_CONNECT) }
            .map_err(|_| "Npcap's service manager state could not be verified")?,
    );
    let service = ServiceHandle(
        unsafe { OpenServiceW(manager.0, w!("npcap"), SERVICE_QUERY_STATUS) }
            .map_err(|_| "Npcap's service could not be opened for verification")?,
    );
    let mut status = SERVICE_STATUS_PROCESS::default();
    let status_bytes = unsafe {
        std::slice::from_raw_parts_mut(
            (&mut status as *mut SERVICE_STATUS_PROCESS).cast::<u8>(),
            std::mem::size_of::<SERVICE_STATUS_PROCESS>(),
        )
    };
    let mut required = 0_u32;
    if !unsafe {
        QueryServiceStatusEx(
            service.0,
            SC_STATUS_PROCESS_INFO,
            Some(status_bytes),
            &mut required,
        )
    }
    .as_bool()
        || required as usize > status_bytes.len()
    {
        return Err("Npcap's running service state could not be verified".into());
    }
    Ok(InstalledService {
        running: status.dwCurrentState == SERVICE_RUNNING,
        start_type: registry_dword_at(w!(r"SYSTEM\CurrentControlSet\Services\npcap"), w!("Start"))?,
        image_path: registry_expand_string(
            w!(r"SYSTEM\CurrentControlSet\Services\npcap"),
            w!("ImagePath"),
        )?,
    })
}

struct ServiceHandle(SC_HANDLE);

impl Drop for ServiceHandle {
    fn drop(&mut self) {
        unsafe {
            CloseServiceHandle(self.0);
        }
    }
}

fn installed_settings() -> Result<ApprovedSettings, String> {
    Ok(ApprovedSettings {
        admin_only: registry_dword(w!("AdminOnly"))?,
        winpcap_compatible: registry_dword(w!("WinPcapCompatible"))?,
        loopback_support: registry_dword(w!("LoopbackSupport"))?,
        dlt_null: registry_dword(w!("DltNull"))?,
        dot11_support: registry_dword(w!("Dot11Support"))?,
    })
}

fn registry_dword(name: PCWSTR) -> Result<u32, String> {
    registry_dword_at(
        w!(r"SYSTEM\CurrentControlSet\Services\npcap\Parameters"),
        name,
    )
}

fn registry_dword_at(subkey: PCWSTR, name: PCWSTR) -> Result<u32, String> {
    let mut value = 0_u32;
    let mut size = std::mem::size_of::<u32>() as u32;
    let result = unsafe {
        RegGetValueW(
            HKEY_LOCAL_MACHINE,
            subkey,
            name,
            RRF_RT_REG_DWORD,
            None,
            Some((&mut value as *mut u32).cast()),
            Some(&mut size),
        )
    };
    if result == ERROR_SUCCESS && size == std::mem::size_of::<u32>() as u32 {
        Ok(value)
    } else {
        Err("Npcap's reviewed settings could not be verified".into())
    }
}

fn registry_expand_string(subkey: PCWSTR, name: PCWSTR) -> Result<String, String> {
    let flags = RRF_RT_REG_EXPAND_SZ | RRF_NOEXPAND;
    let mut size = 0_u32;
    let first = unsafe {
        RegGetValueW(
            HKEY_LOCAL_MACHINE,
            subkey,
            name,
            flags,
            None,
            None,
            Some(&mut size),
        )
    };
    if first != ERROR_SUCCESS || !(2..=2048).contains(&size) || size % 2 != 0 {
        return Err("Npcap's service image path could not be sized safely".into());
    }
    let mut value = vec![0_u16; size as usize / 2];
    let second = unsafe {
        RegGetValueW(
            HKEY_LOCAL_MACHINE,
            subkey,
            name,
            flags,
            None,
            Some(value.as_mut_ptr().cast()),
            Some(&mut size),
        )
    };
    if second != ERROR_SUCCESS || size < 2 || size as usize > value.len() * 2 || size % 2 != 0 {
        return Err("Npcap's service image path could not be read safely".into());
    }
    let units = size as usize / 2;
    value.truncate(units);
    if value.last() == Some(&0) {
        value.pop();
    }
    String::from_utf16(&value).map_err(|_| "Npcap's service image path was invalid".into())
}

fn sha256(path: &Path) -> Result<String, String> {
    let mut file = File::open(path).map_err(|_| "a reviewed Npcap file is missing")?;
    let mut digest = Sha256::new();
    let mut buffer = [0_u8; 64 * 1024];
    loop {
        let read = file
            .read(&mut buffer)
            .map_err(|_| "a reviewed Npcap file could not be read")?;
        if read == 0 {
            break;
        }
        digest.update(&buffer[..read]);
    }
    Ok(digest
        .finalize()
        .iter()
        .map(|byte| format!("{byte:02X}"))
        .collect())
}

fn default_route_device() -> Result<String, String> {
    let mut index = 0_u32;
    if unsafe { GetBestInterface(u32::from_ne_bytes([1, 1, 1, 1]), &mut index) } != ERROR_SUCCESS.0
    {
        return Err("Windows could not identify the default network adapter".into());
    }
    let mut luid = NET_LUID_LH::default();
    if unsafe { ConvertInterfaceIndexToLuid(index, &mut luid) } != ERROR_SUCCESS {
        return Err("Windows could not validate the default network adapter".into());
    }
    let mut guid = GUID::zeroed();
    if unsafe { ConvertInterfaceLuidToGuid(&luid, &mut guid) } != ERROR_SUCCESS {
        return Err("Windows could not name the default network adapter".into());
    }
    Ok(device_name(guid))
}

fn device_name(guid: GUID) -> String {
    format!(r"\Device\NPF_{{{guid:?}}}")
}

fn system32_path() -> Result<PathBuf, String> {
    let mut buffer = vec![0_u16; 32_768];
    let length = unsafe { GetSystemDirectoryW(Some(&mut buffer)) } as usize;
    if length == 0 || length >= buffer.len() {
        return Err("Windows System32 could not be located".into());
    }
    Ok(PathBuf::from(OsString::from_wide(&buffer[..length])))
}

fn loaded_module_path(module: HMODULE) -> Result<PathBuf, String> {
    let mut buffer = vec![0_u16; 32_768];
    let length = unsafe { GetModuleFileNameW(module, &mut buffer) } as usize;
    if length == 0 || length >= buffer.len() {
        return Err("a loaded Npcap module could not be verified".into());
    }
    Ok(PathBuf::from(OsString::from_wide(&buffer[..length])))
}

fn preloaded_module(name: PCWSTR) -> bool {
    unsafe { GetModuleHandleW(name) }.is_ok()
}

fn same_path(left: &Path, right: &Path) -> bool {
    left.as_os_str()
        .to_string_lossy()
        .eq_ignore_ascii_case(&right.as_os_str().to_string_lossy())
}

fn wide_path(path: &Path) -> Vec<u16> {
    path.as_os_str().encode_wide().chain([0]).collect()
}

fn checked_capture_length(captured: u32, original: u32, data_is_null: bool) -> Option<usize> {
    if captured > SNAPLEN as u32 || original != captured || (captured > 0 && data_is_null) {
        None
    } else {
        Some(captured as usize)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::{cell::RefCell, fs::OpenOptions, io::Write, ptr::NonNull, sync::Mutex};

    static NPCAP_DLL_TEST_LOCK: Mutex<()> = Mutex::new(());

    #[derive(Clone, Copy, Debug, Eq, PartialEq)]
    enum FailAt {
        None,
        Create,
        Snaplen,
        Promisc,
        Timeout,
        Buffer,
        Immediate,
        ActivateWarning,
        ActivateError,
        Datalink,
        Compile,
        SetFilter,
        Nonblock,
    }

    struct FakeSetup {
        fail_at: FailAt,
        calls: RefCell<Vec<&'static str>>,
    }

    impl FakeSetup {
        fn new(fail_at: FailAt) -> Self {
            Self {
                fail_at,
                calls: RefCell::new(Vec::new()),
            }
        }

        fn count(&self, call: &str) -> usize {
            self.calls
                .borrow()
                .iter()
                .filter(|item| **item == call)
                .count()
        }

        fn setter(&self, name: &'static str, fail_at: FailAt) -> c_int {
            self.calls.borrow_mut().push(name);
            (self.fail_at == fail_at) as c_int
        }
    }

    impl CaptureCloser for FakeSetup {
        fn close_capture(&self, _: *mut Pcap) {
            self.calls.borrow_mut().push("close");
        }
    }

    impl CaptureSetupApi for FakeSetup {
        fn create_capture(&self, _: *const c_char, _: *mut c_char) -> *mut Pcap {
            self.calls.borrow_mut().push("create");
            if self.fail_at == FailAt::Create {
                std::ptr::null_mut()
            } else {
                NonNull::<Pcap>::dangling().as_ptr()
            }
        }

        fn set_snaplen(&self, _: *mut Pcap, _: c_int) -> c_int {
            self.setter("snaplen", FailAt::Snaplen)
        }

        fn set_promisc(&self, _: *mut Pcap, _: c_int) -> c_int {
            self.setter("promisc", FailAt::Promisc)
        }

        fn set_timeout(&self, _: *mut Pcap, _: c_int) -> c_int {
            self.setter("timeout", FailAt::Timeout)
        }

        fn set_buffer_size(&self, _: *mut Pcap, _: c_int) -> c_int {
            self.setter("buffer", FailAt::Buffer)
        }

        fn set_immediate_mode(&self, _: *mut Pcap, _: c_int) -> c_int {
            self.setter("immediate", FailAt::Immediate)
        }

        fn activate(&self, _: *mut Pcap) -> c_int {
            self.calls.borrow_mut().push("activate");
            match self.fail_at {
                FailAt::ActivateWarning => 1,
                FailAt::ActivateError => -1,
                _ => 0,
            }
        }

        fn datalink(&self, _: *mut Pcap) -> c_int {
            self.calls.borrow_mut().push("datalink");
            if self.fail_at == FailAt::Datalink {
                101
            } else {
                DLT_EN10MB
            }
        }

        fn compile_filter(
            &self,
            _: *mut Pcap,
            program: *mut BpfProgram,
            _: *const c_char,
            _: c_int,
            _: c_uint,
        ) -> c_int {
            self.calls.borrow_mut().push("compile");
            if self.fail_at == FailAt::Compile {
                -1
            } else {
                unsafe {
                    (*program).length = 1;
                    (*program).instructions = NonNull::<BpfInsn>::dangling().as_ptr();
                }
                0
            }
        }

        fn set_filter(&self, _: *mut Pcap, _: *mut BpfProgram) -> c_int {
            self.setter("setfilter", FailAt::SetFilter)
        }

        fn free_filter(&self, _: *mut BpfProgram) {
            self.calls.borrow_mut().push("free");
        }

        fn set_nonblock(&self, _: *mut Pcap, _: c_int, _: *mut c_char) -> c_int {
            self.setter("nonblock", FailAt::Nonblock)
        }
    }

    struct TestFiles(Vec<PathBuf>);

    impl Drop for TestFiles {
        fn drop(&mut self) {
            for path in &self.0 {
                let _ = std::fs::remove_file(path);
            }
        }
    }

    fn create_test_files(paths: Vec<PathBuf>) -> TestFiles {
        for path in &paths {
            let mut file = OpenOptions::new()
                .write(true)
                .create_new(true)
                .open(path)
                .unwrap();
            file.write_all(b"Pengo test decoy; not a DLL").unwrap();
        }
        TestFiles(paths)
    }

    fn ethernet(ether_type: u16, payload: &[u8]) -> Vec<u8> {
        let mut frame = vec![0_u8; 12];
        frame.extend_from_slice(&ether_type.to_be_bytes());
        frame.extend_from_slice(payload);
        frame
    }

    fn ipv4_packet(protocol: u8, source_port: u16, destination_port: u16) -> Vec<u8> {
        let transport_length = if protocol == 17 { 8 } else { 20 };
        let mut packet = vec![0_u8; 20 + transport_length];
        let total_length = packet.len() as u16;
        packet[0] = 0x45;
        packet[2..4].copy_from_slice(&total_length.to_be_bytes());
        packet[8] = 64;
        packet[9] = protocol;
        packet[12..16].copy_from_slice(&[192, 0, 2, 1]);
        packet[16..20].copy_from_slice(&[198, 51, 100, 2]);
        packet[20..22].copy_from_slice(&source_port.to_be_bytes());
        packet[22..24].copy_from_slice(&destination_port.to_be_bytes());
        if protocol == 17 {
            packet[24..26].copy_from_slice(&(transport_length as u16).to_be_bytes());
        } else {
            packet[32] = 0x50;
        }
        ethernet(0x0800, &packet)
    }

    fn ipv6_udp(source_port: u16, destination_port: u16) -> Vec<u8> {
        let mut packet = vec![0_u8; 48];
        packet[0] = 0x60;
        packet[4..6].copy_from_slice(&8_u16.to_be_bytes());
        packet[6] = 17;
        packet[7] = 64;
        packet[23] = 1;
        packet[39] = 2;
        packet[40..42].copy_from_slice(&source_port.to_be_bytes());
        packet[42..44].copy_from_slice(&destination_port.to_be_bytes());
        packet[44..46].copy_from_slice(&8_u16.to_be_bytes());
        ethernet(0x86dd, &packet)
    }

    fn offline_matches(
        api: &PcapApi,
        program: &BpfProgram,
        frame: &[u8],
        captured_length: usize,
        original_length: usize,
    ) -> bool {
        let header = PcapHeader {
            timestamp: PcapTimeval {
                seconds: 0,
                microseconds: 0,
            },
            captured_length: captured_length as u32,
            original_length: original_length as u32,
        };
        unsafe { (api.offline_filter)(program, &header, frame.as_ptr()) != 0 }
    }

    #[test]
    fn every_setup_failure_closes_and_frees_exactly_once() {
        let device = CString::new(r"\Device\NPF_{00000000-0000-0000-0000-000000000000}").unwrap();
        let filter = CString::new(GI_FILTER).unwrap();
        for (failure, expected_close, expected_free) in [
            (FailAt::Create, 0, 0),
            (FailAt::Snaplen, 1, 0),
            (FailAt::Promisc, 1, 0),
            (FailAt::Timeout, 1, 0),
            (FailAt::Buffer, 1, 0),
            (FailAt::Immediate, 1, 0),
            (FailAt::ActivateWarning, 1, 0),
            (FailAt::ActivateError, 1, 0),
            (FailAt::Datalink, 1, 0),
            (FailAt::Compile, 1, 0),
            (FailAt::SetFilter, 1, 1),
            (FailAt::Nonblock, 1, 1),
        ] {
            let api = FakeSetup::new(failure);
            assert!(
                configure_capture(&api, &device, &filter).is_err(),
                "{failure:?}"
            );
            assert_eq!(api.count("close"), expected_close, "{failure:?}");
            assert_eq!(api.count("free"), expected_free, "{failure:?}");
        }

        let api = FakeSetup::new(FailAt::None);
        let handle = configure_capture(&api, &device, &filter).unwrap();
        assert_eq!(api.count("close"), 0);
        assert_eq!(api.count("free"), 1);
        api.close_capture(handle.as_ptr());
        assert_eq!(api.count("close"), 1);
    }

    #[test]
    fn approval_gate_is_exact_and_fail_closed() {
        assert!(installation_files_and_settings_are_approved(
            APPROVED_WPCAP_SHA256,
            APPROVED_PACKET_SHA256,
            APPROVED_DRIVER_SHA256,
            APPROVED_SETTINGS,
        ));
        assert!(!installation_files_and_settings_are_approved(
            &"0".repeat(64),
            APPROVED_PACKET_SHA256,
            APPROVED_DRIVER_SHA256,
            APPROVED_SETTINGS,
        ));
        assert!(!installation_files_and_settings_are_approved(
            APPROVED_WPCAP_SHA256,
            APPROVED_PACKET_SHA256,
            APPROVED_DRIVER_SHA256,
            ApprovedSettings {
                admin_only: 1,
                ..APPROVED_SETTINGS
            },
        ));
        assert!(reported_version_is_approved(APPROVED_VERSION.as_bytes()));
        assert!(!reported_version_is_approved(b"Npcap version 1.89"));

        let approved_service = InstalledService {
            running: true,
            start_type: SERVICE_SYSTEM_START.0,
            image_path: APPROVED_SERVICE_IMAGE.into(),
        };
        assert!(service_is_approved(&approved_service));
        for rejected in [
            InstalledService {
                running: false,
                ..approved_service.clone()
            },
            InstalledService {
                start_type: 2,
                ..approved_service.clone()
            },
            InstalledService {
                image_path: r"C:\Temp\npcap.sys".into(),
                ..approved_service.clone()
            },
        ] {
            assert!(!service_is_approved(&rejected));
        }
    }

    #[test]
    fn default_route_guid_maps_to_one_npcap_adapter_name() {
        let guid = GUID::from_u128(0x00112233_4455_6677_8899_aabbccddeeff);
        assert_eq!(
            device_name(guid),
            r"\Device\NPF_{00112233-4455-6677-8899-AABBCCDDEEFF}"
        );
    }

    #[test]
    fn capture_contract_is_narrow_and_bounded() {
        assert_eq!(GI_FILTER, "udp and (port 22101 or port 22102)");
        assert_eq!(HSR_FILTER, "udp and (port 23301 or port 23302)");
        assert_eq!(filter_for_game(Game::Gi), GI_FILTER);
        assert_eq!(filter_for_game(Game::Hsr), HSR_FILTER);
        assert_eq!(SNAPLEN, 9_000);
        assert_eq!(KERNEL_BUFFER_BYTES, 1024 * 1024);
        assert_eq!(DLT_EN10MB, 1);
        assert_eq!(checked_capture_length(9_000, 9_000, false), Some(9_000));
        assert_eq!(checked_capture_length(9_001, 9_001, false), None);
        assert_eq!(checked_capture_length(8_999, 9_000, false), None);
        assert_eq!(checked_capture_length(1, 1, true), None);
    }

    #[test]
    fn actual_compiled_filters_accept_only_the_selected_games_udp_frames() {
        let _lock = NPCAP_DLL_TEST_LOCK.lock().unwrap();
        let api = PcapApi::load().unwrap();
        let dead = NonNull::new(unsafe { (api.open_dead)(DLT_EN10MB, SNAPLEN) }).unwrap();
        let dead_guard = CaptureConstructionGuard::new(&api, dead);
        for (filter_text, ports, other_ports) in [
            (GI_FILTER, [22_101, 22_102], [23_301, 23_302]),
            (HSR_FILTER, [23_301, 23_302], [22_101, 22_102]),
        ] {
            let filter = CString::new(filter_text).unwrap();
            let mut program = BpfProgram::default();
            assert_eq!(
                api.compile_filter(
                    dead.as_ptr(),
                    &mut program,
                    filter.as_ptr(),
                    1,
                    PCAP_NETMASK_UNKNOWN,
                ),
                0
            );
            let filter_guard = FilterProgramGuard { api: &api, program };

            for frame in [
                ipv4_packet(17, ports[0], 443),
                ipv4_packet(17, 443, ports[1]),
                ipv6_udp(ports[1], 443),
                ipv6_udp(443, ports[0]),
            ] {
                assert!(offline_matches(
                    &api,
                    &filter_guard.program,
                    &frame,
                    frame.len(),
                    frame.len(),
                ));
            }
            for frame in [
                ipv4_packet(17, other_ports[0], 443),
                ipv6_udp(443, other_ports[1]),
                ipv4_packet(17, 30_000, 30_001),
                ipv6_udp(30_000, 30_001),
                ipv4_packet(6, ports[0], 443),
            ] {
                assert!(!offline_matches(
                    &api,
                    &filter_guard.program,
                    &frame,
                    frame.len(),
                    frame.len(),
                ));
            }
            let destination_match = ipv4_packet(17, 443, ports[1]);
            assert!(!offline_matches(
                &api,
                &filter_guard.program,
                &destination_match,
                37,
                destination_match.len(),
            ));
            let malformed = ethernet(0x0800, &[0_u8; 8]);
            assert!(!offline_matches(
                &api,
                &filter_guard.program,
                &malformed,
                malformed.len(),
                malformed.len(),
            ));
        }

        drop(dead_guard);
        api.unload().unwrap();
    }

    #[test]
    fn approved_paths_are_only_below_system32_npcap() {
        let paths = approved_paths().unwrap();
        let system32 = system32_path().unwrap();
        assert_eq!(paths.wpcap, system32.join("Npcap").join("wpcap.dll"));
        assert_eq!(paths.packet, system32.join("Npcap").join("Packet.dll"));
        assert_eq!(paths.driver, system32.join("drivers").join("npcap.sys"));
        assert!(!same_path(
            &std::env::current_dir().unwrap().join("wpcap.dll"),
            &paths.wpcap,
        ));
    }

    #[test]
    fn reviewed_dll_loads_without_capture_and_preloading_is_rejected() {
        let _lock = NPCAP_DLL_TEST_LOCK.lock().unwrap();
        let executable = std::env::current_exe().unwrap();
        let sibling = executable.parent().unwrap();
        let local_name = format!(
            "{}.local",
            executable.file_name().unwrap().to_string_lossy()
        );
        let _decoys = create_test_files(vec![
            sibling.join("wpcap.dll"),
            sibling.join("Packet.dll"),
            sibling.join(local_name),
        ]);
        let api = PcapApi::load().unwrap();
        api.unload().unwrap();

        let path = approved_paths().unwrap().wpcap;
        let wide = wide_path(&path);
        let module = unsafe {
            LoadLibraryExW(
                PCWSTR(wide.as_ptr()),
                None,
                LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32,
            )
        }
        .unwrap();
        let result = PcapApi::load();
        assert!(matches!(result, Err(ref error) if error.contains("already loaded")));
        unsafe {
            FreeLibrary(module);
        }
    }
}
