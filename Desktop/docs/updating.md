# Packaging updates

Run packaging from `Desktop` in normal PowerShell. Neither command publishes or signs a file.

Development stays local and keeps `packageUrl` empty:

```powershell
& .\packaging\build-development-package.ps1 -Version 1.4.0.0
```

Stable packaging is allowed only when the worktree is clean and `HEAD` has exactly one tag, which must be strict `vMAJOR.MINOR` or `vMAJOR.MINOR.PATCH`:

```powershell
& .\packaging\build-stable-package.ps1
```

The stable script derives `MAJOR.MINOR.PATCH.0`, checks the app and updater file versions, seals the fixed Pengo stable URL, and prints the tag, commit, channel, version, artifact, size, and SHA-256. `-NoRestore` and `-Force` work on both commands. Supplying `-Version` to the stable script only checks that it exactly matches the tag.

Review the unsigned hash before using either artifact:

```powershell
Get-FileHash .\packaging\artifacts\Nyx-Desktop-1.4.0.0-development-win-x64.zip -Algorithm SHA256
Get-FileHash .\packaging\artifacts\Nyx-Desktop-1.4.0.0-stable-win-x64.zip -Algorithm SHA256
```

Both channels remain unsigned and may trigger Windows SmartScreen. Stable metadata is not signing and is not proof of publication.
