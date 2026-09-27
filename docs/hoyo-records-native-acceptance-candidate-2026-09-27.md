# HoYo records native acceptance candidate

This unmerged branch prepares development package **1.8.0.21** for the next authorized native acceptance session. It is based on `99ff38dd5ffeacf18e290f2ce1f5612071ec1181`, whose integrated Windows build, 3,233 .NET tests, 223 capture-script tests and 109 portable Rust tests passed in [run 36279962022](https://github.com/Asyce/Nyx-Launcher/actions/runs/36279962022). The two prescribed installed-Npcap cases were excluded. All 406 files of that base development package were independently verified.

The user has stopped desktop/browser control. Building, reviewing and verifying this candidate does not authorize launching it, installing it, opening private sessions, changing account settings, running capture, or arming a profile-isolation wrapper. No user action is requested by this preparation.

## Candidate scope

Only the three existing native availability gates are enabled: Genshin Abyss, HSR Forgotten Hall/Pure Fiction/Apocalyptic Shadow, and ZZZ selected-role sync with owned equipped Agents and Shiyu. Their receivers were published before this candidate; the native collectors, protected stores, encryption and consent flows are already on the base commit. This branch changes no collector, wire schema, account/profile boundary, export path or sync defaults. Newly introduced Remember choices and ZZZ automatic sync remain off by default. The user's reported HSR automatic preference must be preserved.

The source guard assertions follow this explicit candidate configuration. Normal `main` retains all three gates as false. The branch workflow supplies an explicit development version to the existing package builder; it creates a sealed development artifact with no update URL. No version tag, stable package, public release or production change is authorized by this document.

This candidate is not eligible for merge or release on CI success alone. Keep its pull request in draft until the exact changed native paths have evidence. After acceptance, reconcile the comments, candidate-only workflow version and final release documentation before a separately justified release.

## Next authorized session

1. Recheck the exact candidate source, CI receipt, distribution/payload checksums, all sealed files and the installed/profile state using the existing preservation procedure. This preparation does not arm or run that procedure. Select the candidate by its source and version; installed/public 1.8 and the ordinary 1.4 CI build do not contain the enabled candidate paths.
2. Use the already prepared Europe roles on the same HoYo account. Reuse accepted login, value parity, recovery, deletion and manual-sync evidence. Do not create accounts, manufacture records, spend, rotate recovery or repeat destructive tests.
3. For Genshin, explicitly opt into Remember Spiral Abyss records, refresh once, and confirm current/previous periods survive protected save and the existing encrypted sync/view flow. Missing or unavailable periods must remain distinct from zero. Follow the [Abyss contract](hoyolab-genshin-abyss-source-contract-2026-09-22.md).
4. For HSR, explicitly opt into Remember Star Rail challenge records, refresh once, and confirm all three current/previous challenge families through native save and sync. Follow the [HSR contract](hoyolab-hsr-challenges-source-contract-2026-09-22.md). Anomaly Arbitration, universe modes and Currency Wars remain outside this qualified slice.
5. For ZZZ, use the selected role and separately opt into Remember characters/equipped builds and Shiyu records. Confirm the complete owned-Agent collection and both supported Shiyu periods through save and manual sync. Keep ZZZ automatic sync off unless the user chooses it. Follow the [ZZZ contract](hoyolab-zzz-populated-source-contract-2026-09-22.md); full-bag gear and other modes remain unqualified.
6. For the remaining automatic-upload predicate, preserve the user's HSR ON preference. Use one ordinary resource refresh after the persisted hourly window or one supported full refresh, then confirm the updated observation in My HoYo without manually initiating that upload. The last observed My HoYo session was locked; only the user may supply the needed unlock interaction. Record outcome, capability and timestamps, never identifiers, recovery codes or bodies.
7. Restore the user's chosen settings and the established profile/installation arrangement. Record only the changed-path outcomes and any narrow failure. Do not infer any of these outcomes from source tests, a visible switch, browser observations or successful packaging.

The broader R1–R11 goal remains unfinished. This package does not qualify current-game full bags, new capture APIs, complete RE-Factor records, an Endfield achievement catalog, other HoYo modes/inventory, factual-source retirement, legacy-reader retirement or a stable release.
