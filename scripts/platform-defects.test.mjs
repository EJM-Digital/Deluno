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

test("DOK-22: a native Linux host without Docker has a supervised way to run Deluno (a systemd unit, documented)", {
  skip: "GAP (DOK-22): there is no package, service file or documented supervision for `dotnet Deluno.Host.dll` on a native Linux host; docs/ names systemd only as a model for the tray supervisor."
}, () => {
  assert.match(read("docs/background-services.md") + read("README.md"), /\[Service\][\s\S]*ExecStart=.*Deluno\.Host\.dll/);
});

test("FRS-08: a new Windows install has no automatic update check or download on until the guide's Updates question is answered", {
  skip: "DECISION (FRS-08): AppSettings defaults AutoCheckUpdates to true and UpdateMode to download-in-the-background (apps/windows-tray/AppSettings.cs lines 12-17) before the guide's Updates question is answered, against the nothing-on-by-default rule (#936). The owner decides; a passing test then pins the choice."
}, () => {
  assert.match(read("apps/windows-tray/AppSettings.cs"), /public bool AutoCheckUpdates \{ get; set; \} = false;/);
});

test("GAT-03: the Windows tray tests run in CI, so a push that skipped the local gate cannot ship a tray regression behind a green run", {
  skip: "DEFECT (GAT-03): ci.yml runs no job that executes tests/Deluno.Tray.Tests (the project compiles to an empty assembly on ubuntu); only the local gate and repeat-nightly.yml run them."
}, () => {
  const ci = read(".github/workflows/ci.yml");
  assert.match(ci, /runs-on: windows-latest[\s\S]{0,4000}Deluno\.Tray\.Tests/);
});

