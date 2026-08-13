"""Verify the reviewed Windows release binary's imports and sensitive strings."""

from pathlib import Path
import shutil
import struct
import subprocess
import sys
import tempfile

import pefile


binary = Path(sys.argv[1] if len(sys.argv) > 1 else "target/release/pengo-achievements-launcher.exe")
pe = pefile.PE(str(binary), fast_load=False)
if pe.OPTIONAL_HEADER.Subsystem != 2:
    raise SystemExit(f"launcher helper must use the Windows GUI/no-console subsystem, got {pe.OPTIONAL_HEADER.Subsystem}")
imports = {
    entry.name.decode("ascii")
    for module in pe.DIRECTORY_ENTRY_IMPORT
    for entry in module.imports
    if entry.name
}
imported_modules = {
    module.dll.decode("ascii").lower() for module in pe.DIRECTORY_ENTRY_IMPORT
}
for forbidden_module in imported_modules:
    if forbidden_module == "vcruntime140.dll" or forbidden_module.startswith("api-ms-win-crt-"):
        raise SystemExit(f"release did not use the static CRT: {forbidden_module}")
    if forbidden_module in {"wpcap.dll", "packet.dll"}:
        raise SystemExit(f"Npcap must be explicitly loaded after review gates: {forbidden_module}")

# IMAGE_LOAD_CONFIG_DIRECTORY64.DependentLoadFlags is the USHORT at offset
# 0x4e. pefile 2024 labels this newer SDK field Reserved1, so read the PE bytes
# directly and require LOAD_LIBRARY_SEARCH_SYSTEM32 (0x800).
load_config = pe.OPTIONAL_HEADER.DATA_DIRECTORY[10]
if load_config.VirtualAddress == 0:
    raise SystemExit("missing PE load configuration")
load_config_offset = pe.get_offset_from_rva(load_config.VirtualAddress)
load_config_size = struct.unpack_from("<I", pe.__data__, load_config_offset)[0]
if load_config_size < 0x50:
    raise SystemExit(f"PE load configuration is too short: {load_config_size:#x}")
dependent_load_flags = struct.unpack_from("<H", pe.__data__, load_config_offset + 0x4E)[0]
if dependent_load_flags != 0x800:
    raise SystemExit(
        f"unsafe PE dependent load flags: {dependent_load_flags:#x}; expected System32-only 0x800"
    )

# RT_MANIFEST (24) must be an actual embedded PE resource. Windows ignores
# executable-name `.local` DLL redirection when an application manifest is
# present, so checking the source file alone would not protect the release.
RT_MANIFEST = 24
resource_types = {
    entry.id
    for entry in getattr(
        getattr(pe, "DIRECTORY_ENTRY_RESOURCE", None), "entries", []
    )
}
if RT_MANIFEST not in resource_types:
    raise SystemExit("release has no embedded application manifest")
required = {
    "ConvertInterfaceIndexToLuid",
    "ConvertInterfaceLuidToGuid",
    "CloseServiceHandle",
    "FlushFileBuffers",
    "GetBestInterface",
    "GetDriveTypeW",
    "GetFileAttributesW",
    "GetModuleFileNameW",
    "GetModuleHandleW",
    "GetSystemDirectoryW",
    "GetSystemTime",
    "LoadLibraryExW",
    "MoveFileExW",
    "OpenSCManagerW",
    "OpenEventW",
    "OpenMutexW",
    "OpenServiceW",
    "QueryServiceStatusEx",
    "RegGetValueW",
    "SHGetKnownFolderPath",
    "SetDefaultDllDirectories",
    "WaitForSingleObject",
}
missing = required - imports
if missing:
    raise SystemExit(f"missing hardened PE imports: {sorted(missing)}")
data = binary.read_bytes()
for required_string in (
    b"udp and (port 22101 or port 22102)",
    b"udp and (port 23301 or port 23302)",
    b"Npcap version 1.88, based on libpcap version 1.10.6 (64-bit time_t)",
    b"D1CA7FCF9128D02A75EAF29CE9A9D85C5697377460F92420D976DA187521CF39",
    b"2793CE72F0E04D5885AAEE1273A7373441D01934B2CFF3886B031C13CA826345",
    b"13D598E277E9C7BF43688D7087EF9B944E8036561A1E7169D31D9EC1D38F9A01",
    b"\\SystemRoot\\system32\\DRIVERS\\npcap.sys",
    b"--output-root",
    b"named-event",
    b"named-mutex",
    b"named-pipe",
    b"Pengo.Nyx.AchievementIpc.v1.",
    b"Local\\Pengo.Nyx.ExportCancel.v1.",
    b"Local\\Pengo.Nyx.ExportParent.v1.",
):
    if required_string not in data:
        raise SystemExit(f"reviewed Npcap release gate is missing: {required_string!r}")
for forbidden in (
    b"before decryption",
    b"after decryption",
    b"message data:",
    b"Found encryption key seed",
    b"setting new session seed",
    b"possible session seeds",
    b"field: ",
    b"--output-file",
    b"--force",
    b"pcap_dump",
    b"pcap_sendpacket",
    b"pcap_open_offline",
    b"pcap_open_dead",
    b"pcap_offline_filter",
):
    if forbidden in data or forbidden.decode().encode("utf-16le") in data:
        raise SystemExit(f"sensitive release string remains: {forbidden!r}")

projection_note = "; windows 0.48 projection delay-loader imports LoadLibraryA" if "LoadLibraryA" in imports else ""

# Exercise the actual release image from a directory containing invalid sibling
# DLLs. A vulnerable startup import would load one of these before main().
with tempfile.TemporaryDirectory(prefix="pengo-achievements-pe-") as temporary:
    test_dir = Path(temporary)
    test_binary = test_dir / binary.name
    shutil.copy2(binary, test_binary)
    for name in ("VCRUNTIME140.dll", "bcryptprimitives.dll", "wpcap.dll", "Packet.dll"):
        (test_dir / name).write_bytes(b"Pengo PE verifier decoy; not a DLL")
    (test_dir / f"{binary.name}.local").write_bytes(b"Pengo DLL redirection decoy")
    # Malformed launcher input is intentionally silent. In particular, never
    # reflect a caller-supplied URL or token into coordinator logs.
    secret = "https://secret.invalid/?token=DO_NOT_PRINT"
    malformed = subprocess.run(
        [
            test_binary,
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
        ],
        cwd=test_dir,
        capture_output=True,
        text=True,
        timeout=15,
        check=False,
    )
    if malformed.returncode != 2 or malformed.stdout or malformed.stderr:
        raise SystemExit(
            "release reflected malformed launcher input: "
            f"exit={malformed.returncode}, stdout={malformed.stdout!r}, stderr={malformed.stderr!r}"
        )

print(
    f"PE gate passed: {len(imports)} imports; no-console GUI subsystem; static CRT; embedded manifest; dependent-load System32 flag; "
    f"sibling and .local DLL decoys ignored; hardened APIs present; malformed launcher input silent; sensitive strings absent{projection_note}"
)
