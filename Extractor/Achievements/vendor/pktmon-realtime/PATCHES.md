# Pengo realtime-only patch

Source: `pktmon` 0.6.2 from crates.io, MIT licensed.

Pengo vendors only the Windows 11 realtime files. `src/lib.rs` removes the
legacy ETL backend, ETL reader, stream helper, and automatic fallback. A failed
realtime initialization is an error. No global legacy filters are installed.

The fork also:

- resolves `PktMonApi.dll` from `GetSystemDirectoryW`, loads it with
  `LoadLibraryExW` plus `LOAD_LIBRARY_SEARCH_SYSTEM32`, and verifies the loaded
  module path, keeping the module guard armed until every export resolves;
- treats callback event kinds as raw C integers and rejects values outside the
  documented `0..=3` range before reading their union payload;
- unwinds partially constructed handles in stream/session/monitor order;
- rejects null, oversized, overflowing, or out-of-buffer callback descriptors;
- bounds the callback queue;
- removes unused optional API entry points so startup requires only functions
  used by the one realtime capture path;
- removes the library's global console callback and copied raw handles so only
  the RAII capture owner can stop and unload; and
- disables release logging at compile time.

The crate-level Clippy allowances cover unchanged pktmon 0.6.2 binding/filter
idioms only. Pengo's root crate and this fork otherwise pass with warnings
denied.

The windows 0.48 projection contributes an internal `LoadLibraryA` import for
its own delay-loader. The Pengo source never calls it. Tests place a decoy DLL
beside the test executable and prove the PktMon module still resolves to the
exact System32 path.

The path test intentionally does not initialize Packet Monitor or require the
new realtime exports on the test machine; unsupported Windows builds fail
cleanly when `Capture::new` resolves the required realtime functions.
