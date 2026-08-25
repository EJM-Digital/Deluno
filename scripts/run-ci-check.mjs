#!/usr/bin/env node
// Launches scripts/ci-check.ps1 with PowerShell 7 (pwsh) when it is installed,
// falling back to Windows PowerShell (powershell) so the pre-push gate also
// runs on Windows machines without pwsh. The .ps1 itself supports both.
import { spawnSync } from "node:child_process";

const args = ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "scripts/ci-check.ps1"];

for (const shell of ["pwsh", "powershell"]) {
  const result = spawnSync(shell, args, { stdio: "inherit" });
  if (result.error && result.error.code === "ENOENT") {
    continue;
  }
  process.exit(result.status ?? 1);
}

console.error("ci:check needs pwsh (PowerShell 7) or powershell (Windows PowerShell) on PATH; neither was found.");
process.exit(1);
