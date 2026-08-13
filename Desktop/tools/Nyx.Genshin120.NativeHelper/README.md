# Nyx Genshin 120 FPS native helper

This directory is intentionally isolated. It builds two x64 native files:

- `Nyx.Genshin120.Stub.dll`, embedded into the helper and never packaged loose;
- `Nyx.Genshin120.Helper.exe`, a hidden elevated helper with the static C++ runtime.

Run `build.ps1`, then `verify-release.ps1`. Building does not start Genshin or
change user state.

## Boundary protocol (version 1)

The helper command line is exactly one canonical GUID. It connects to
`\\.\pipe\Pengo.Nyx.Genshin120.<guid>`. Nyx owns the private pipe and sends:

1. packed `RequestHeader` from `src/Protocol.h`;
2. payload: three little-endian `uint32` values (`exeChars`, `rootChars`,
   `argumentCount`), the fixed 32-byte SHA-256 Nyx calculated while validating
   the executable before elevation, then the UTF-16 executable and root without nulls;
3. for each ordered argument, one little-endian `uint32` character count and
   UTF-16 value without a null.

There is no FPS, DLL, environment, URL, or shell field. The response is the
packed fixed `Response`. Its status is one of `Ready`,
`GameStartedAttachFailed`, `GameStartedAttachTimedOut`, `InvalidRequest`, or
`StartFailure`. The process exit code is the same status number.

After elevation, the helper independently requires a valid cached-only Windows
Authenticode signature from exactly `COGNOSPHERE PTE. LTD.` on the pinned
`GenshinImpact.exe` handle. The caller SHA-256 only binds the pre-elevation and
post-elevation file identity. Pipe reads and writes are cancellable and bounded.
