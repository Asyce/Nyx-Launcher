# Nyx Launcher agent instructions

- This repository is the standalone Nyx Windows launcher. Do not add build or
  runtime dependencies on a sibling `Nyx` checkout.
- Launcher source, tests, packaging, and scripts live under `Desktop/`.
- Keep generated launcher snapshots and their exact build inputs inside this
  repository; runtime refreshes must use fixed Pengo-owned HTTPS endpoints,
  validate before promotion, and retain the last known good copy on failure.
- Preserve normal-user execution. Do not weaken the existing account, launch,
  cache, reparse-point, helper-hash, or external-link boundaries.
- Before pushing, run:

```powershell
dotnet test Desktop/Nyx.Desktop.slnx --configuration Release --no-restore --verbosity minimal
dotnet build Desktop/src/Nyx.Desktop.App/Nyx.Desktop.App.csproj --configuration Release --no-restore
dotnet build Desktop/src/Nyx.Desktop.Infrastructure/Nyx.Desktop.Infrastructure.csproj --configuration Release --no-restore
git diff --check
```
