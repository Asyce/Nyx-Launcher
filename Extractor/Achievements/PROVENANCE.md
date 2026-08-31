# Provenance

Pinned on 2026-08-03 for this branch-only test build.
Stardb public key maps refreshed on 2026-08-31; other pins are unchanged.

The user attested that Stardb's owner directly permitted Pengo/Nyx to reuse the
public Stardb key maps and extractor behavior. The two maps came from Stardb
v2.21.0 commit `50c04597d37cf366290de6e316aaca98dd57acfc`:

| Map | Source path | Entries | Upstream raw SHA-256 | Canonical JSON SHA-256 |
| --- | --- | ---: | --- | --- |
| GI | `keys/gi.json` | 10 | `e0e1fcbfb6aa5d727367a60574b7688a4da14abe12c5a3bdad3a7fc87c694d18` | `37ccd359c35b0f990032e7941ed140914a322b935706a1c66d252b27dd74f3c3` |
| HSR | `keys/hsr.json` | 30 | `85a98f5abf9b4041d6752e8f60b6db760d5a9753ad73874a9d5744f9c1d7944a` | `8ffac930c0ff2821c0d8f9c0bcbcdaba64a8be0395c6263572c3c5afa65d34ec` |

`apply_patch` changed only JSON line endings. Tests hash the sorted canonical
JSON, so any changed, missing, or extra key fails. The raw hashes above identify
the exact upstream files.

The v2.21.0 refresh adds exactly one HSR entry and preserves all 29 v2.20.0
entries byte-for-byte; GI upstream bytes are unchanged. A regression removes
only the added HSR ID and checks the previous canonical hash, and checks the
added value's standard base64 encoding and 4096-byte decoded length. This is
static key-map compatibility, not proof of live HSR 4.5 achievement or gear
capture compatibility; parser, capture, and export behavior remain unchanged.

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
| Genshin Optimizer | `984d82cda1e37a3a634ab14d2059b6ad91b90a4a` | `contracts/gear-export-genshin-good-v3.fixture.json` | `3f91ecb188798db18de8e782ce88360a4f37864d37c55285957a91af8f8d1f64` |

Both fixtures are synthetic, identity-free, and used only for contract tests.
Local pinned-consumer acceptance passed at the exact commits above:

- HSR: a one-test Vitest wrapper instantiated
  `KelzFormatParser(ReliquaryArchiverConfig)` over the exact fixture: 1/1
  passed.
- Genshin: a one-test Vitest wrapper called `parseGOODImport` with
  `ArtCharDatabase` and `SandboxStorage` over the exact fixture: 1/1 passed.

At exact pin `984d82c`, the parser accepts missing optional `initialValue` and
preserves it absent. This is a fixture/parser observation, not a
packet-semantics claim.

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

## Genshin 7.0 offline artifact map

At the audited pin, Dimbreath/animegamedata2 has no repository license. On
2026-08-27 the user confirmed the necessary rightsholder approval for this
exact HoYo data use. That approval is limited to generating this
Pengo-generated offline mapping; it is not a public code license. The
generator copies no upstream code, uses only Python's standard library,
requires explicit local source roots, and never fetches at build time or
runtime.

The approved raw source is
[`Dimbreath/animegamedata2`](https://gitlab.com/Dimbreath/animegamedata2/-/tree/26df1dfbdf05a82bbb1d97506859f3e1c40718d8)
at commit `26df1dfbdf05a82bbb1d97506859f3e1c40718d8`:

| Source path | Rows | SHA-256 |
| --- | ---: | --- |
| `ExcelBinOutput/ReliquaryExcelConfigData.json` | 4,352 | `1b0ea4e5642f183d579e1f2701359a5e5afebfc886f7379d0f6ddf3dc7d9b4e5` |
| `ExcelBinOutput/ReliquaryMainPropExcelConfigData.json` | 66 | `c7c9ea5520fd090a090c0c7a12e750ac85fd80985a687194a30b9aa254d5c60b` |
| `ExcelBinOutput/ReliquaryAffixExcelConfigData.json` | 350 | `0e1f1461d86597b3126b4f9ed61ee8975a7839e47111060458e51ae1b756bc39` |

The validation source is
[`frzyc/genshin-optimizer`](https://github.com/frzyc/genshin-optimizer/tree/984d82cda1e37a3a634ab14d2059b6ad91b90a4a)
at commit `984d82cda1e37a3a634ab14d2059b6ad91b90a4a`:

| Source path | SHA-256 |
| --- | --- |
| `libs/gi/dm/src/mapping/artifact.ts` | `0619c7e58d77d04c5f3da37649f4bb860dbe6d9d2c18aeec016ee6ca16facda3` |
| `libs/gi/consts/src/artifact.ts` | `704ea84c1555e999ad6057e822e29922c58cfbc0cd7d8c52a616af9d5fc35781` |
| `libs/gi/dm/src/dm/character/AvatarExcelConfigData_idmap_gen.json` | `1c8f30d9aa78c0ad8afcd3f27bb3c0cecb6e26409174c6238a476d15a7b3c12e` |
| `libs/gi/consts/src/character.ts` | `1594571fb4a96c184f99e0f424313ff2c1ea8c749abd50a1b38f1dfde2962fdc` |

Hashes are over UTF-8 bytes after one Windows-safe CRLF-to-LF conversion.
Bare carriage returns are rejected. The generated map contains 3,520 item
rows, a 625-ID low-rarity allowlist, 56 main-property rows, 198
active/unactivated affix rows, and 124 character IDs covering 119 character
keys. All 29 referenced main-property depots and all 12 referenced
append-property depots are covered.

The 4,352 raw item rows have exactly these exclusions: 625 one- and two-star
rows, 175 rows from unsupported sets `15000`, `15004`, and `15012`, 32 rows
with no set ID, and zero unexplained rows. Percent properties are converted to
the GOOD percentage scale. Affix rows carry only their mapped `key` and
`value`; no `initialValue` is invented for unactivated or active rows.

The canonical contract is 613,555 UTF-8 bytes with SHA-256
`377e333336e6a94d01785612533c4241a83e49e1d414efe283e1458fefe78b1b`. Its
offline check covers canonical bytes, pins, counts, sorted IDs, depot
coverage, exclusions, and synthetic lookups including item `31533`, main
property `13007`, affixes `501022`, `501201`, `501241`, `501221`, character
`10000061`, and rejection of affix `401021` when used with depot `501`.

This mapping is checked-in static data only. It is not embedded in the helper
or package and does not enable gear export. It contains no user export,
account or game file, capture, packet, log, token, key, or network behavior.
Any later public packaging requires a separate permission and notice review.
