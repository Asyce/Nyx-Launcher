# Nyx Launcher

This is the standalone source repository for the Nyx Windows launcher. The
launcher can be started directly from this repository for development. It is
an unpackaged, self-contained Windows app: it does not need temporary package
identity or Developer Mode.

## Requirements

- Windows 11 22H2 or newer (build 22621+), x64.
- The x64 .NET SDK version pinned in `global.json` (currently `10.0.100`).
- A normal PowerShell window. Nyx itself stays a normal-user app; do not run the wrapper or PowerShell as administrator.

## Commands

Run these from the repository root in a normal PowerShell window.

Restore packages only when the checked-in restore assets are missing or dependencies changed:

```powershell
dotnet restore Desktop\src\Nyx.Desktop.App\Nyx.Desktop.App.csproj -r win-x64
```

Build the reviewed x64 app without restoring:

```powershell
dotnet build Desktop\src\Nyx.Desktop.App\Nyx.Desktop.App.csproj -c Release -r win-x64 -p:Platform=x64 --no-restore
```

Check that this PC and checkout are ready. This does not restore, register, or start anything:

```powershell
& .\Desktop\scripts\start-nyx.ps1 -CheckOnly
```

Build and start the reviewed unpackaged app:

```powershell
& .\Desktop\scripts\start-nyx.ps1
```

If restore assets are missing, a real start can explicitly allow the normal .NET restore first:

```powershell
& .\Desktop\scripts\start-nyx.ps1 -Restore
```

You can also double-click `Desktop\Start Nyx.cmd`. The wrapper calls only the fixed repository-relative start script and forwards no command text.

## Live launcher content

On every run, Nyx checks the official HoYo, Kuro, and GRYPHLINK launcher sources for the current game backgrounds. Verified files are cached locally so the last good background still works offline. Banners and redemption codes refresh automatically from the Pengo feed.

Nyx itself remains normal-user software. Only a sealed, revalidated direct launch of Genshin, HSR, or ZZZ can ask Windows for administrator approval when that exact game requires it. Official launchers and arbitrary paths can never use this boundary.
