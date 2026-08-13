use std::{
    ffi::{OsString, c_void},
    mem::transmute,
    os::windows::ffi::{OsStrExt, OsStringExt},
    path::{Path, PathBuf},
};

use windows::Win32::Foundation::{GetLastError, HANDLE, HMODULE};
use windows::Win32::System::{
    LibraryLoader::{
        FreeLibrary, GetModuleFileNameW, GetProcAddress, LOAD_LIBRARY_SEARCH_SYSTEM32,
        LoadLibraryExW,
    },
    SystemInformation::GetSystemDirectoryW,
};
use windows::core as win;
use windows::{
    core::{HRESULT, PCWSTR},
    s,
};

use super::c_filter::PacketMonitorProtocolConstraint;

// TODO: Use bind-gen bindings

#[repr(transparent)]
#[derive(Copy, Clone, Debug)]
pub struct SendHandle(pub *mut c_void);
unsafe impl Send for SendHandle {}
unsafe impl Sync for SendHandle {}
impl Default for SendHandle {
    fn default() -> Self {
        Self(std::ptr::null_mut())
    }
}

#[repr(transparent)]
#[derive(Copy, Clone, Debug, Default)]
pub struct PacketMonitorHandle(pub SendHandle);

#[repr(transparent)]
#[derive(Copy, Clone, Debug, Default)]
pub struct PacketMonitorSession(SendHandle);

#[repr(transparent)]
#[derive(Copy, Clone, Debug, Default)]
pub struct PacketMonitorRealTimeStream(pub SendHandle);

#[repr(C)]
#[derive(Copy, Clone, Debug)]
pub struct PacketMonitorStreamStartInfoOut {
    pub packet_buffer_size_in_bytes: u32,
    pub truncation_size: u16,
}

#[repr(C)]
#[derive(Copy, Clone, Debug)]
pub struct PacketMonitorStreamStopInfoOut {
    pub is_fatal_error: bool,
    pub reason: u32,
}

#[repr(C)]
#[derive(Copy, Clone, Debug)]
pub struct PacketMonitorStreamProcessInfoOut {
    pub is_warning: bool,
    pub reason: u32,
    pub packet_length: u64,
}

#[repr(C)]
#[derive(Copy, Clone)]
pub union PacketMonitorStreamEventInfo {
    pub stream_start_info: PacketMonitorStreamStartInfoOut,
    pub stream_stop_info: PacketMonitorStreamStopInfoOut,
    pub stream_process_info: PacketMonitorStreamProcessInfoOut,
}

#[repr(C)]
#[derive(Copy, Clone, Debug)]
#[allow(dead_code, non_camel_case_types)]
pub enum PacketMonitorPacketType {
    PktMonPayload_Unknown,
    PktMonPayload_Ethernet,
    PktMonPayload_WiFi,
    PktMonPayload_IP,
    PktMonPayload_HTTP,
    PktMonPayload_TCP,
    PktMonPayload_UDP,
    PktMonPayload_ARP,
    PktMonPayload_ICMP,
    PktMonPayload_ESP,
    PktMonPayload_AH,
    PktMonPayload_L4Payload,
}

impl TryFrom<u16> for PacketMonitorPacketType {
    type Error = ();
    fn try_from(value: u16) -> Result<Self, Self::Error> {
        match value {
            x if x == Self::PktMonPayload_Unknown as u16 => Ok(Self::PktMonPayload_Unknown),
            x if x == Self::PktMonPayload_Ethernet as u16 => Ok(Self::PktMonPayload_Ethernet),
            x if x == Self::PktMonPayload_WiFi as u16 => Ok(Self::PktMonPayload_WiFi),
            x if x == Self::PktMonPayload_IP as u16 => Ok(Self::PktMonPayload_IP),
            x if x == Self::PktMonPayload_HTTP as u16 => Ok(Self::PktMonPayload_HTTP),
            x if x == Self::PktMonPayload_TCP as u16 => Ok(Self::PktMonPayload_TCP),
            x if x == Self::PktMonPayload_UDP as u16 => Ok(Self::PktMonPayload_UDP),
            x if x == Self::PktMonPayload_ARP as u16 => Ok(Self::PktMonPayload_ARP),
            x if x == Self::PktMonPayload_ICMP as u16 => Ok(Self::PktMonPayload_ICMP),
            x if x == Self::PktMonPayload_ESP as u16 => Ok(Self::PktMonPayload_ESP),
            x if x == Self::PktMonPayload_AH as u16 => Ok(Self::PktMonPayload_AH),
            x if x == Self::PktMonPayload_L4Payload as u16 => Ok(Self::PktMonPayload_L4Payload),
            _ => Err(()),
        }
    }
}

pub type PacketMonitorStreamEventCallback = extern "stdcall" fn(
    context: *mut c_void,
    event_info: *const PacketMonitorStreamEventInfo,
    event_kind: u32,
);

pub type PacketMonitorStreamDataCallback =
    extern "stdcall" fn(context: *mut c_void, data: *const PacketMonitorStreamDataDescriptor);

#[repr(C)]
#[derive(Copy, Clone, Debug)]
pub struct PacketMonitorStreamDataDescriptor {
    pub data: *const c_void,
    pub data_size: u32,

    pub metadata_offset: u32,
    pub packet_offset: u32,
    pub packet_length: u32,
    pub missed_packet_write_count: u32,
    pub missed_packet_read_count: u32,
}

#[repr(C)]
#[derive(Copy, Clone, Debug)]
pub struct PacketMonitorStreamMetadata {
    pub pkt_group_id: u64,
    pub pkt_count: u16,
    pub appearance_count: u16,
    pub direction_name: u16,
    pub packet_type: u16,
    pub component_id: u16,
    pub edge_id: u16,
    pub reserved: u16,
    pub drop_reason: u32,
    pub drop_location: u32,
    pub processor: u16,
    pub timestamp: i64,
}

#[repr(C)]
#[derive(Copy, Clone, Debug)]
pub struct PacketMonitorRealTimeStreamConfiguration {
    pub user_context: *mut c_void,
    pub event_callback: *mut PacketMonitorStreamEventCallback,
    pub data_callback: *mut PacketMonitorStreamDataCallback,
    pub buffer_size_multiplier: u16,
    pub truncation_size: u16,
}

pub const PACKETMONITOR_API_VERSION: u32 = 0x00010000;

#[derive(Debug)]
#[allow(dead_code)]
pub struct PacketMonitorApi {
    module: HMODULE,

    pub initialize: extern "stdcall" fn(
        api_version: u32,
        reserved: *mut c_void,
        handle: *mut PacketMonitorHandle,
    ) -> HRESULT,
    pub uninitialize: extern "stdcall" fn(handle: PacketMonitorHandle),
    pub create_live_session: extern "stdcall" fn(
        handle: PacketMonitorHandle,
        name: PCWSTR,
        session: *mut PacketMonitorSession,
    ) -> HRESULT,
    pub set_session_active:
        extern "stdcall" fn(session: PacketMonitorSession, active: bool) -> HRESULT,
    pub create_realtime_stream: extern "stdcall" fn(
        handle: PacketMonitorHandle,
        configuration: *const PacketMonitorRealTimeStreamConfiguration,
        realtime_stream: *mut PacketMonitorRealTimeStream,
    ) -> HRESULT,
    pub attach_output_to_session: extern "stdcall" fn(
        session: PacketMonitorSession,
        output_handle: PacketMonitorRealTimeStream,
    ) -> HRESULT,
    pub close_session_handle: extern "stdcall" fn(session: PacketMonitorSession),
    pub close_realtime_stream: extern "stdcall" fn(realtime_stream: PacketMonitorRealTimeStream),
    pub add_capture_constraint: extern "stdcall" fn(
        session: PacketMonitorSession,
        capture_constraint: *const PacketMonitorProtocolConstraint,
    ) -> HRESULT,
}

impl PacketMonitorApi {
    pub fn new() -> win::Result<Self> {
        unsafe {
            let expected = system_module_path()?;
            let expected_wide = expected
                .as_os_str()
                .encode_wide()
                .chain([0])
                .collect::<Vec<_>>();
            let module = LoadLibraryExW(
                PCWSTR(expected_wide.as_ptr()),
                HANDLE(0),
                LOAD_LIBRARY_SEARCH_SYSTEM32,
            )?;
            let mut module_guard = ModuleGuard(Some(module));
            let loaded = loaded_module_path(module)?;
            if !same_module_path(&expected, &loaded) {
                return Err(invalid_module_error());
            }

            macro_rules! get_proc_address {
                ($name:expr) => {
                    transmute(
                        GetProcAddress(module, s!($name))
                            .ok_or_else(|| win::Error::from(GetLastError()))?,
                    )
                };
            }

            // Resolve every required export while ModuleGuard still owns the DLL.
            // If any lookup fails, the guard unloads the partially validated module.
            let initialize = get_proc_address!("PacketMonitorInitialize");
            let uninitialize = get_proc_address!("PacketMonitorUninitialize");
            let create_live_session = get_proc_address!("PacketMonitorCreateLiveSession");
            let set_session_active = get_proc_address!("PacketMonitorSetSessionActive");
            let create_realtime_stream = get_proc_address!("PacketMonitorCreateRealtimeStream");
            let attach_output_to_session = get_proc_address!("PacketMonitorAttachOutputToSession");
            let close_session_handle = get_proc_address!("PacketMonitorCloseSessionHandle");
            let close_realtime_stream = get_proc_address!("PacketMonitorCloseRealtimeStream");
            let add_capture_constraint = get_proc_address!("PacketMonitorAddCaptureConstraint");
            let module = module_guard
                .0
                .take()
                .expect("module guard must own the module");

            Ok(PacketMonitorApi {
                module,
                initialize,
                uninitialize,
                create_live_session,
                set_session_active,
                create_realtime_stream,
                attach_output_to_session,
                close_session_handle,
                close_realtime_stream,
                add_capture_constraint,
            })
        }
    }
}

fn invalid_module_error() -> win::Error {
    win::Error::new(
        HRESULT(0x8007_0057_u32 as i32),
        "PktMonApi.dll was not loaded from System32".into(),
    )
}

fn system_module_path() -> win::Result<PathBuf> {
    let mut buffer = vec![0_u16; 32_768];
    let length = unsafe { GetSystemDirectoryW(Some(&mut buffer)) } as usize;
    if length == 0 || length >= buffer.len() {
        return Err(invalid_module_error());
    }
    let mut path = PathBuf::from(OsString::from_wide(&buffer[..length]));
    path.push("PktMonApi.dll");
    Ok(path)
}

fn loaded_module_path(module: HMODULE) -> win::Result<PathBuf> {
    let mut buffer = vec![0_u16; 32_768];
    let length = unsafe { GetModuleFileNameW(module, &mut buffer) } as usize;
    if length == 0 || length >= buffer.len() {
        return Err(invalid_module_error());
    }
    Ok(PathBuf::from(OsString::from_wide(&buffer[..length])))
}

fn same_module_path(expected: &Path, loaded: &Path) -> bool {
    expected
        .as_os_str()
        .to_string_lossy()
        .eq_ignore_ascii_case(&loaded.as_os_str().to_string_lossy())
}

struct ModuleGuard(Option<HMODULE>);

impl Drop for ModuleGuard {
    fn drop(&mut self) {
        if let Some(module) = self.0.take() {
            unsafe { FreeLibrary(module) };
        }
    }
}

impl Drop for PacketMonitorApi {
    fn drop(&mut self) {
        unsafe {
            FreeLibrary(self.module);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn system32_path_gate_rejects_a_sibling_name() {
        let expected = system_module_path().unwrap();
        let sibling = std::env::current_dir().unwrap().join("PktMonApi.dll");
        assert!(!same_module_path(&expected, &sibling));
        let expected_parent = expected.parent().unwrap();
        assert!(same_module_path(
            expected_parent,
            system_module_path().unwrap().parent().unwrap()
        ));
    }

    #[test]
    fn loaded_module_resolves_to_system32_with_a_sibling_decoy() {
        struct Decoy(PathBuf);
        impl Drop for Decoy {
            fn drop(&mut self) {
                let _ = std::fs::remove_file(&self.0);
            }
        }
        let sibling = std::env::current_exe()
            .unwrap()
            .parent()
            .unwrap()
            .join("PktMonApi.dll");
        std::fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&sibling)
            .unwrap();
        let _decoy = Decoy(sibling);
        let expected = system_module_path().unwrap();
        let expected_wide = expected
            .as_os_str()
            .encode_wide()
            .chain([0])
            .collect::<Vec<_>>();
        let module = unsafe {
            LoadLibraryExW(
                PCWSTR(expected_wide.as_ptr()),
                HANDLE(0),
                LOAD_LIBRARY_SEARCH_SYSTEM32,
            )
        }
        .unwrap();
        let guard = ModuleGuard(Some(module));
        assert!(same_module_path(
            &expected,
            &loaded_module_path(module).unwrap()
        ));
        drop(guard);
    }
}
