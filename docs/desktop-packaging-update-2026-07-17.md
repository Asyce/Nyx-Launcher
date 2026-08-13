# Nyx Desktop packaging and update boundary

Date: 2026-07-17

Status: local unsigned development distribution implemented and verified; public signing and publishing not authorized

## Result

`Desktop/packaging/build-development-package.ps1` produces a deterministic outer ZIP for the fixed x64 development lane. The bundle is installable per Windows user through a reviewed PowerShell entry point, creates a Start menu shortcut, records release notes/version/first-run defaults, and has a separate uninstaller. Nyx remains non-administrator.

`%LOCALAPPDATA%\Pengo\Nyx` is the one user-data root used by both the app and updater. On first start after upgrading an older local build, the app audits `%LOCALAPPDATA%\Nyx`, including every ancestor and every item below it, then moves that complete legacy directory to the canonical root with one same-volume directory rename. It never copies, merges, overwrites, follows links, or deletes migration input. A reparse point, inaccessible component, non-directory collision, or two existing roots stops startup with both roots untouched.

Uninstall removes the program and shortcut but retains both the canonical and legacy roots by default. Only the explicit `-RemoveUserData` switch removes those two fixed roots. Both data trees are audited before any program, shortcut, or data change, so a link or file collision in either root fails the whole preflight. A legitimate two-root migration conflict is removed only after that explicit request and successful audit. Deletion then opens and retains non-delete-sharing Windows handles for the drive root, every ancestor, and each directory being walked. The final component is opened without following a reparse point and the already-open file or directory is deleted through that handle. Concurrent rename, replacement, or link substitution therefore either cannot occur or is rejected before traversal; deletion never reopens an audited name as authority.

The existing launcher-state migration remains the authority for first-run and upgraded user state. Packaging tests bind the packaged defaults to runtime defaults and cover preservation of selection, custom games, user art, rail order, and export output paths across the v0-to-v1 migration. Packaging and updates never place user data inside the replaceable app tree.

## Trust boundaries and failure behavior

The outer ZIP is only a carrier. `release.json` is the update contract. It binds product, channel, exact four-part version, architecture, payload filename, byte count, payload SHA-256, fixed entry point, and the sorted complete file tree with per-file sizes and SHA-256 values.

Preview/stable URLs are restricted to HTTPS default port, no user information/query/fragment, exact host `pengo.gg`, and exact channel/package path. Development may omit a URL for local testing. The updater has no network client and cannot interpret a command line through PowerShell or `cmd`.

The updater fails closed on malformed/oversized manifests, unknown fields, unsafe or case-colliding paths, ZIP traversal, links/reparse points, extra/missing entries, size mismatch, whole-package hash mismatch, or inner-file hash mismatch. It writes extracted bytes only under a newly created staging directory and removes incomplete staging output.

Apply verifies the staged tree again, takes an exclusive lock, and durably writes a same-volume transaction journal before its first directory rename. The journal records the fixed stage and rollback names plus each completed phase. Updater startup reconciles a stop before or after every rename, phase write, pending-marker write, or journal removal by comparing the journal with the exact expected folders. Impossible combinations, file collisions, and links fail closed without guessing. Rollback and failed-first-install abandonment are journaled the same way. Another update cannot start while one is pending. Confirmation verifies the active files again; user-data directories are never part of any update transaction.

Custom-game definitions loaded from JSON are not execution authority. Startup registers only definitions that still have absolute local paths, existing files, safe arguments, and no reparse point in the drive root, any parent directory, or the selected file. The adapter repeats that complete audit while observing and again immediately before direct process start. Any inspection error or changed/canonicalized path returns `NeedsReview`; no shell is invoked and no process starts.

Installer failure after app placement invokes rollback/uninstall cleanup. The installed uninstaller copies the self-contained updater to a fresh temporary directory before deletion, so it does not try to delete a running executable from inside the install root.

## Commands

```powershell
dotnet test Desktop\Nyx.Desktop.slnx -c Release
& .\Desktop\packaging\build-development-package.ps1 -Version 1.0.0.0
```

The normal package invocation restores dependencies before publishing, so it works from ordinary repository state even after tests rewrite RID assets. `-NoRestore` is an explicit opt-out for callers who have already restored the exact projects and intentionally want to reuse those assets. The package build itself invokes `Nyx.Desktop.Update.exe verify` against the generated manifest and payload before creating the outer ZIP. A failed forced rebuild leaves the last completed artifact in place; replacement happens only after the new artifact and sidecars are complete. Building twice from the same restored source/output inputs must produce the same outer SHA-256.

## Verified development artifact

The completed local release audit built version `1.0.0.116` twice from the same
committed source. Both builds produced the identical outer ZIP:

- File: `Nyx-Desktop-1.0.0.116-development-win-x64.zip`
- Size: `137,402,667` bytes
- SHA-256:
  `9a0e8d05cfd42d8753f60267d717c2eed8a81e2f10f63957d2155f1aad6205ba`
- Payload: 499 files, including 24 current launcher-art assets, the desktop app,
  updater, and verified achievement helper
- Exclusions confirmed: no PDB files and no removed `LatestContent`, Google
  portrait, or legacy launcher-content assets

## External blocker

No public Windows code-signing certificate, certificate account, protected private-key store, publisher decision, timestamping service authorization, or production update route was provided. The current ZIP and contained executables are therefore intentionally unsigned. Public release requires owner authorization plus signing and timestamp verification in a protected CI/release environment. Nothing in this work deploys or publishes a route.
