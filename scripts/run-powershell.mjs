#!/usr/bin/env node
// Runs a repository PowerShell script with whichever PowerShell is installed:
// PowerShell 7 (`pwsh`) when it is there, Windows PowerShell (`powershell`)
// when it is not. Every one of these scripts supports both.
//
// Why this exists: `ci:check` had this fallback and the other three did not, so
// on a machine without PowerShell 7 — including the one Deluno is developed on
// — `npm run soak:snapshot`, `ga:regression` and `validate:agents` all died
// with "'pwsh' is not recognized". The soak collector is a prerequisite of the
// 14-day soak (#82), which is a GA gate, so the gate could not be recorded on
// the only machine that could record it.
//
// One runner rather than four wrappers: the fallback is one decision, and four
// copies of it is four places for the next person to fix it in three of.
import { spawnSync } from "node:child_process";

const [script, ...forwarded] = process.argv.slice(2);

if (!script) {
  console.error("Usage: node scripts/run-powershell.mjs <script.ps1> [args...]");
  process.exit(2);
}

const args = ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, ...forwarded];

for (const shell of ["pwsh", "powershell"]) {
  const result = spawnSync(shell, args, { stdio: "inherit" });

  // Only "this shell is not installed" is worth trying the next one for. A
  // script that ran and failed has already said why, and running it a second
  // time under a different shell would bury that.
  if (result.error && result.error.code === "ENOENT") {
    continue;
  }

  process.exit(result.status ?? 1);
}

console.error(
  `${script} needs pwsh (PowerShell 7) or powershell (Windows PowerShell) on PATH; neither was found.`
);
process.exit(1);
