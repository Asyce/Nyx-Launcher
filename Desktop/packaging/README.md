# Nyx Desktop distributions

This folder builds local Windows distribution artifacts. It does not publish anything and it does not create or use a public signing identity.

## Build

Run a development build from `Desktop` in normal, non-administrator PowerShell:

```powershell
& .\packaging\build-development-package.ps1 -Version 1.4.0.0
```

When the worktree is clean and `HEAD` has exactly one tag, which must be `vMAJOR.MINOR` or `vMAJOR.MINOR.PATCH`, run a stable build without supplying a version:

```powershell
& .\packaging\build-stable-package.ps1
```

The stable build maps `v1.4` to `1.4.0.0` and `v1.4.2` to `1.4.2.0`. Components cannot have leading zeros and must fit from 0 through 65535. A supplied `-Version` is only a cross-check and must match the tag-derived four-part version. The stable manifest uses only `https://pengo.gg/desktop/updates/stable/Nyx-Desktop-<version>-win-x64.zip`. Development manifests keep `packageUrl` empty.

The normal invocation restores dependencies before publishing, so it also works after tests or other commands have rewritten local RID restore assets. Use `-NoRestore` only when the exact projects have already been restored and you intentionally want to reuse those assets. Both scripts forward `-NoRestore` and `-Force`. The build is fixed to Release, `win-x64`, self-contained Windows App SDK output, a fixed ZIP timestamp, sorted entries, and no PDB files. Output goes only to `packaging\artifacts`, with the channel in each artifact name. `-Force` keeps the last artifact in place while the replacement builds and verifies, then replaces only generated files in that folder.

Both channels are unsigned. Compare the printed SHA-256 with the adjacent `.sha256` file, or calculate it directly:

```powershell
Get-FileHash .\packaging\artifacts\Nyx-Desktop-1.4.0.0-development-win-x64.zip -Algorithm SHA256
Get-FileHash .\packaging\artifacts\Nyx-Desktop-1.4.0.0-stable-win-x64.zip -Algorithm SHA256
```

The package build always compiles `pengo-achievements-launcher.exe` from the locked Rust source into its fresh private work directory. It remaps repository, work, Cargo, and user-profile source paths, applies the helper's checked-in Windows hardening config, runs the PE release verifier, computes SHA-256, embeds that exact hash in Nyx, and includes that exact file. Before sealing the payload it rejects private build-path text in every packaged executable and DLL. It never reads `Extractor\Achievements\target`, so an old local helper cannot slip into a package. The runtime rechecks the embedded hash, binds every non-reparse ancestor directory plus the exact helper file identity, and holds those Windows handles until normal `Process.Start` or elevated `ShellExecute` has resolved the path.

The same build clones the fixed official `genshin-fps-unlock` v3.5.0 source into that package run's private work directory, rejects any commit other than `2b85d61dd06f6e11ad86fdd6bd90339f9abc58eb`, verifies every source hash written in the provenance record against that checkout, and compiles the checked-in Nyx reduction with its deterministic native verifier. Nyx receives only `Assets\Tools\Nyx.Genshin120.Helper.exe`, its stamped SHA-256, the upstream MIT notice, and the provenance record. The native FPS component is embedded in the helper; no loose DLL, helper updater, configuration, build output, or source tree enters the payload. This checkout is build-time only: the installed launcher stays offline for this feature.

The generated outer ZIP contains:

- `Install-Nyx.ps1` and `Uninstall-Nyx.ps1`;
- the self-contained `Nyx.Desktop.Update.exe` verifier/updater;
- `release.json`, release notes, and first-run defaults; and
- one payload ZIP whose name, byte count, SHA-256, entry list, per-file sizes, and per-file SHA-256 values are sealed by `release.json`.

The bundled achievement helper is launcher-only and uses the Windows GUI subsystem, so it cannot expose the old console prompt. Only this narrow helper may request Windows approval for HSR capture; Nyx itself stays unelevated. A job-owned cancel event handles normal shutdown, while a parent-owned mutex makes capture cancel if Nyx crashes or is killed.

Extract the outer ZIP, review its hash, then run `Install-Nyx.ps1` as the normal Windows user. Installation is per user under `%LOCALAPPDATA%\Programs\Pengo Nyx`. It creates `Pengo\Nyx Desktop.lnk` in that user's Start menu. It never asks for administrator approval.

Run the installed `control\Uninstall-Nyx.ps1` to uninstall. The default keeps both `%LOCALAPPDATA%\Pengo\Nyx` and the older `%LOCALAPPDATA%\Nyx` root. `-RemoveUserData` is the only path that removes those two fixed roots; it audits both before changing the program, shortcut, or either data root. App startup audits and atomically renames the complete legacy root only when the canonical root does not already exist. Migration conflicts and links fail closed without merging or deleting either root.

## Update contract

`release.json` schema 1 accepts only product `nyx-desktop`, channels `development`, `preview`, or `stable`, exact four-part versions, architecture `win-x64`, the fixed entry point `Nyx.Desktop.App.exe`, lowercase SHA-256 values, sorted non-colliding relative file paths, bounded sizes, and bounded file counts.

Development packages may omit a URL. Preview and stable manifests require exactly:

`https://pengo.gg/desktop/updates/<channel>/<sealed-package-name>`

No user information, alternate port, query, fragment, other host, other scheme, or path variation is accepted. The updater intentionally has no downloader. A future transport may download only the manifest-selected file, then must give the local file to `verify`/`stage`; the updater checks the whole-package SHA-256 before extraction and every file SHA-256 while extracting.

Staging uses a new same-volume directory and rejects absolute paths, `..`, Windows reserved aliases, case collisions, extra/missing ZIP entries, links/reparse points, or size/hash mismatches. Apply rechecks the complete staged tree, takes an exclusive update lock, and durably writes a phase journal before moving anything. It then moves the old `app` directory into `rollback`, moves the verified tree into place, and publishes a pending marker. Updater startup reconciles the journal after a stop between any phase; it accepts only the exact expected folder combination and fails closed on links, collisions, or impossible states. Rollback and first-install abandonment use the same before-mutation journal. A second apply is refused until `confirm` or `rollback`. Confirmation rechecks the installed tree before changing active metadata.

The updater never writes to the separate Nyx user-data roots. Uninstall audits the complete program target and, when data removal is explicit, both fixed data targets before deletion. Deletion retains Windows handles for the root and every ancestor, refuses reparse points when opening each child, prevents rename/replacement while a child is in use, and deletes the already-open object through its handle. Concurrent substitution fails closed instead of turning an earlier name audit into authority. It keeps both current and legacy user data unless removal was explicit.

## Signing boundary

Development and stable artifacts are both unsigned output. A stable channel label does not make a file trusted or published. Windows SmartScreen may warn. A publicly trusted installer requires the owner to choose the publisher identity, buy or provision a Windows code-signing certificate/account, protect that private key outside the repository, sign both installer and updater/app binaries, timestamp them, and add a CI verification/publishing ceremony. None of that authority or key material exists in this workspace, so public signing and production publishing remain blocked by design.
