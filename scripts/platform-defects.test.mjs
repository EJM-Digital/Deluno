// node --test scripts/platform-defects.test.mjs
// Defects found while proving the platform rows. Each is a test of the behaviour Deluno should have, skipped with the reason
// and the evidence, so the day the defect is fixed the skip comes off and the test is the proof. The ones that need a built image are in
// platform-docker.test.mjs.
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { test } from "node:test";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const read = (relative) => readFileSync(path.join(root, relative), "utf8").replace(/\r\n/g, "\n");

test("DOK-07: the container's HEALTHCHECK asks whether Deluno is ready, so a broken database or a full data folder reads unhealthy", () => {
  for (const file of ["Dockerfile", "compose.yaml"]) {
    const text = read(file);
    assert.match(text, /\/api\/health\/ready/, `${file} health check calls the readiness endpoint`);
  }
});

test("DOK-18: the release builds the image for arm64 as well as amd64", () => {
  assert.match(read(".github/workflows/release.yml"), /platforms: linux\/amd64,linux\/arm64|platforms: linux\/arm64,linux\/amd64/);
});

test("DOK-22: a native Linux host without Docker has a supervised way to run Deluno (a systemd unit, documented)", () => {
  // Decided 19 Oct 2026: no systemd unit or non-Docker Linux packaging for now. Docker is the supported Linux path, and the known limit is written
  // down where a Linux user looks, so nobody is left to guess. A day this changes, this test is replaced by one for the unit.
  assert.match(read("README.md"), /Linux:\*\* running Deluno directly on Linux \(as a systemd service, without Docker\) is not supported yet\. On Linux, use Docker/);
  assert.match(read("docs/backup-restore-runbook.md"), /Native Linux \(systemd\) is not supported yet\. On Linux, run Deluno in Docker\./);
});

test("FRS-08: a new Windows install has no automatic update check or download on until the guide's Updates question is answered", () => {
  // An install with no settings file yet is written with the check off (the setup guide asks and turns it on, #1361); the download
  // mode only acts when the check is on (VelopackUpdateOrchestrator). AppSettingsTests (tray tests) load the settings to prove the values.
  const settings = read("apps/windows-tray/AppSettings.cs");
  assert.equal([...settings.matchAll(/AutoCheckUpdates = false/g)].length >= 2, true, "both fresh-install branches write the check off");
  assert.match(read("apps/windows-tray/VelopackUpdateOrchestrator.cs"), /settings\.AutoCheckUpdates && settings\.UpdateMode != UpdateModes\.NotifyOnly/);
});

test("GAT-03: the Windows tray tests run in CI, so a push that skipped the local gate cannot ship a tray regression behind a green run", () => {
  // The tray's tests run in the windows-installer job, which every manual run has, and a tag needs a green manual run on its commit.
  const ci = read(".github/workflows/ci.yml");
  assert.match(ci, /windows-installer:[\s\S]*?name: Test Deluno\.Tray\s+run: dotnet test tests\/Deluno\.Tray\.Tests\/Deluno\.Tray\.Tests\.csproj/);
  assert.match(read("scripts/verify-ci-for-release.mjs"), /windows-installer|Windows installer/);
});
