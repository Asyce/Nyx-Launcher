# Provenance

- Upstream: `https://github.com/34736384/genshin-fps-unlock`
- Release: `v3.5.0`
- Commit: `2b85d61dd06f6e11ad86fdd6bd90339f9abc58eb`
- Licence: MIT; preserved in `LICENSE-THIRD-PARTY.txt`

Reviewed upstream source hashes (SHA-256):

- `UnlockerStub/dllmain.cpp`: `BE87F293E333BB7B931CADB4C3AEE15663190505B978C734400F0CA6755DF614`
- `UnlockerStub/Utils.cpp`: `DB43539D87883686612CBC56E12C4D5E1CA4FCE981F56A234BC4B305095E2E7D`
- `UnlockerStub/Utils.h`: `59B416DDE357967C26760D6F3EA77BAB19F44931D74F254A15E9135850936AD6`

Nyx retains only the upstream FPS-target pattern and pointer-resolution
approach. `src/Stub.cpp` is a clean, FPS-only reduction. It removes upstream
UI, configuration, updater, plugin/DLL injection, HDR, custom resolution,
power saving, mobile UI, Zydis, crash dumps, and logging. The target is fixed
to 120. `src/Helper.cpp` is Nyx-owned boundary, launch, validation, cache, and
result-channel code. `src/Authenticode.cpp` independently rechecks the pinned
file with cached-only Windows Authenticode trust and accepts only the exact
`COGNOSPHERE PTE. LTD.` signer before elevation can launch it.
