# Provenance

Pinned on 2026-08-03 for this branch-only test build.

The user attested that Stardb's owner directly permitted Pengo/Nyx to reuse the
public Stardb key maps and extractor behavior. The two maps came from Stardb
v2.20.0 commit `a0a4d55abf921be4228d6afa94ec0f814549ba16`:

| Map | Source path | Entries | Upstream raw SHA-256 | Canonical JSON SHA-256 |
| --- | --- | ---: | --- | --- |
| GI | `keys/gi.json` | 10 | `e0e1fcbfb6aa5d727367a60574b7688a4da14abe12c5a3bdad3a7fc87c694d18` | `37ccd359c35b0f990032e7941ed140914a322b935706a1c66d252b27dd74f3c3` |
| HSR | `keys/hsr.json` | 29 | `79779916153d42b35771dc5fe6620334726805c9ecd46ecfdfb383d9077a6b85` | `e9381b6b79fd2a41dd3c7ade82508c5eafec9f19e15d7fa2bc0e4a7bcdd42512` |

`apply_patch` changed only JSON line endings. Tests hash the sorted canonical
JSON, so any changed, missing, or extra key fails. The raw hashes above identify
the exact upstream files.

Pinned parser forks are vendored in this folder so their release behavior can
be audited without relying on a moving Git checkout:

- `vendor/auto-artifactarium`: commit `04421c4f8a7ed7e7b65bb5e6e59231d4e98405cf`
- `vendor/auto-reliquary`: commit `bc23b48cb3b1b994a5d4405cefea42eb0e1d3735`
- `vendor/mhy-kcp`: commit `1acf4ba5938ff91f7f2d2a31e16bf1f8d2db9c8f`

Pengo's fork patches remove captured-field printing and all packet,
plaintext/ciphertext, and session-seed tracing. Parser logging is compile-time
disabled in release builds. Each vendor folder records its exact changes.

Artifactarium embeds two upstream static game-protocol RSA private-key files to
decode an encrypted dispatch field. They are public assets from the pinned MIT
source, not a Pengo user secret and not a player's login credential. The
retained LF-normalized PEM source hashes are:

| File | PEM bytes | PEM SHA-256 | Decoded PKCS#1 DER SHA-256 |
| --- | ---: | --- | --- |
| `private_key_4.pem` | 1,678 | `c43fafade9dbc63440339fab24fa19d5ae78bc69e60d66ee956d951d6ff6392f` | `e27f729e1944a7550b51d27b3c3bf4b680209cb982413d3245d56df2ae7f0602` |
| `private_key_5.pem` | 1,678 | `6a3fbd53387f9d13230f8558e40df18ad3a8fc11fc23da83a202eedc3bd70ce3` | `b4ab7873b89540628de48a250747d0746f3c76e64a17b77dad221578a60fd996` |

On a Windows checkout Git may render the same PEM text as 1,704 CRLF bytes.
Tests normalize line endings before checking the retained source hashes.

Packet capture starts from `emmachase/pktmon` commit
`33d1c0c421ed8610540bae3e34da3c1182cf28a2` and its crates.io `pktmon` 0.6.2
archive SHA-256
`138ba8229225b0334707e461dee957b8bbb0ca61c9be21d773991443e4364a08`. Pengo
vendors its Windows
11 realtime files, loads its DLL only from verified System32, bounds callback
descriptors, and removes the legacy ETL backend, fallback, and competing raw
console shutdown hook. See
`vendor/pktmon-realtime/PATCHES.md` and its retained `LICENSE`.

The final PE still imports `LoadLibraryA` through windows 0.48's internal
projection delay-loader. No Pengo or vendored capture/parser source calls it.
The PktMon loader itself uses only the absolute, verified System32 path and
`LoadLibraryExW`. Before any other startup work, the executable applies
`SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32)`. A non-elevated test
proves both `PktMonApi.dll` and `dbghelp.dll` ignore fake sibling files beside
the test executable.

The release build also uses Rust's static MSVC runtime and linker
`/DEPENDENTLOADFLAG:0x800`. The PE verifier requires no VCRUNTIME/UCRT DLL
imports, reads `IMAGE_LOAD_CONFIG_DIRECTORY64.DependentLoadFlags` directly,
and runs a copied release executable beside invalid `VCRUNTIME140.dll` and
`bcryptprimitives.dll` decoys. This protects dependencies loaded before
`main`; `bcryptprimitives.dll` remains a direct System32-only import.

The embedded released catalogs are built from Nyx's local database and pinned
by tests:

| Catalog | IDs | LF-normalized raw SHA-256 |
| --- | ---: | --- |
| GI | 1,844 | `34b5f76579e435249e456ff4eba6a767f8562275f24270ee6111d0f46bfd268e` |
| HSR | 1,869 | `1686a1deb2a03e758e1047684acc9e760d5c793b2e2717bb4d1bc9eeb7c60502` |

The build accepts only the repository's exact reviewed JSON bytes after the
single Windows-safe conversion from CRLF to LF. A bare carriage return,
changed field, changed ID, missing row, duplicate ID, or extra row still fails
the build. This avoids a false hash failure when Git checks out the same files
with Windows line endings.

## Optimizer manual-import schema pins

Pengo adapted only the manual-import schema shapes accepted by these pinned
consumers. No optimizer code was copied or adapted.

| Consumer | Reviewed commit | Test-only contract fixture | Fixture SHA-256 |
| --- | --- | --- | --- |
| HSR Optimizer / Fribbels | `99790f5514159655eb9865de612c7cdec01ae097` | `contracts/gear-export-hsr-fribbels-v4.fixture.json` | `8b22587549c236134d6f3acba9b96b11ca000ad7273bdc1053cc903ec96ad9dc` |
| Genshin Optimizer | `984d82cda1e37a3a634ab14d2059b6ad91b90a4a` | `contracts/gear-export-genshin-good-v3.fixture.json` | `6b14c58f5d752f754cbc356dd4ba8335a698bb5e2ccbe64ca4b8b71f8ee0e8d5` |

Both fixtures are synthetic, identity-free, and used only for contract tests.
Local pinned-consumer acceptance passed at the exact commits above:

- HSR: a one-test Vitest wrapper instantiated
  `KelzFormatParser(ReliquaryArchiverConfig)` over the exact fixture: 1/1
  passed.
- Genshin: a one-test Vitest wrapper called `parseGOODImport` with
  `ArtCharDatabase` and `SandboxStorage` over the exact fixture: 1/1 passed.

These checks do not claim that the launcher implements gear export or that a
gear-export feature has been released.

## Npcap fallback review pin

Pengo does not bundle or install Npcap. The private GI/HSR capture path accepts
the user's installed Npcap only when all of these reviewed values match exactly:

- `Npcap version 1.88, based on libpcap version 1.10.6 (64-bit time_t)`
- `wpcap.dll`: `D1CA7FCF9128D02A75EAF29CE9A9D85C5697377460F92420D976DA187521CF39`
- `Packet.dll`: `2793CE72F0E04D5885AAEE1273A7373441D01934B2CFF3886B031C13CA826345`
- `npcap.sys`: `13D598E277E9C7BF43688D7087EF9B944E8036561A1E7169D31D9EC1D38F9A01`
- settings: `AdminOnly=0`, `WinPcapCompatible=1`, `LoopbackSupport=1`,
  `DltNull=1`, and `Dot11Support=0`
- service: running, System-start (`Start=1`), with exact image path
  `\SystemRoot\system32\DRIVERS\npcap.sys`

The reviewed files had valid Microsoft WHCP (`npcap.sys`) and Nmap Software LLC
(`wpcap.dll` and `Packet.dll`) signatures when pinned. Runtime SHA-256 checks
bind the accepted files to that review. No Npcap source or binary is copied into
this repository. The game enum selects one of two compiled filters only:
`udp and (port 22101 or port 22102)` for GI and
`udp and (port 23301 or port 23302)` for HSR.
