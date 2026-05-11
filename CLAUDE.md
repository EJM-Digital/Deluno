# Deluno — Claude Code

## Pre-push gate (mandatory)

Run this before every `git push`. No exceptions.

```bash
bash scripts/ci-check.sh
```

It restores + builds the backend (Release), builds the frontend, and validates the
agent-readiness map. Takes ~60 s. Catches the same errors GitHub Actions catches.
**Do not push unless this script exits 0.**

## Windows tray project

`apps/windows-tray/` targets `net10.0-windows`. A MSBuild condition excludes all source
files on non-Windows so the Linux CI build succeeds. This means:

- Solution-wide `dotnet build` on Linux silently skips tray source — it will not catch tray errors.
- Always validate tray changes separately before pushing:
  ```bash
  dotnet build apps/windows-tray/Deluno.Tray.csproj
  ```
  (run this on Windows where the project actually compiles)

## Branch strategy

- Active development branches off `main`.
- PR target is always `main`.
- The `feature/windows-packaging` branch contains the Windows tray app and installer.

## Key docs

Full guidance lives in `AGENTS.md` and the `docs/` tree. This file covers only the
rules specific to working with Claude Code.
