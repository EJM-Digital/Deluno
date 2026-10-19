// node --test scripts/platform-defects.test.mjs
// Defects found while proving the platform rows. Each is a test of the behaviour Deluno should have, skipped with the reason
// and the evidence, so the day the defect is fixed the skip comes off and the test is the proof. Docker ones run against the image
// named by DELUNO_DOCKER_IMAGE (Linux or WSL with docker).
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { test } from "node:test";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const read = (relative) => readFileSync(path.join(root, relative), "utf8").replace(/\r\n/g, "\n");
const image = process.env.DELUNO_DOCKER_IMAGE ?? "deluno:dev";

function docker(...args) {
  const result = spawnSync("docker", args, { encoding: "utf8" });
  return { status: result.status, out: `${result.stdout ?? ""}${result.stderr ?? ""}`.trim() };
}

function waitFor(check, seconds) {
  const until = Date.now() + seconds * 1000;
  while (Date.now() < until) {
    if (check()) return true;
    spawnSync(process.execPath, ["-e", "setTimeout(()=>{},1000)"]);
  }
  return false;
}

test("DOK-08: a data folder Deluno cannot write ends with a plain sentence saying so, not an unhandled exception and exit code 139", {
  skip: "DEFECT (DOK-08): with /data read-only (or owned by another user) the container logs a stack trace ('SQLite Error 14: unable to open database file', or 'Access to the path /data/secrets is denied') and exits with code 139. Nothing says in plain words that the data folder is not writable."
}, () => {
  const name = `deluno-defect-dok08-${process.pid}`;
  const volume = `${name}-data`;
  try {
    docker("volume", "create", volume);
    docker("run", "--detach", "--name", name, "--mount", `type=volume,src=${volume},dst=/data`, "--user", "4242:4242", image);
    assert.ok(waitFor(() => docker("inspect", "--format", "{{.State.Status}}", name).out === "exited", 60), "the container stops");
    assert.notEqual(docker("inspect", "--format", "{{.State.ExitCode}}", name).out, "139");
    const log = docker("logs", name).out;
    assert.doesNotMatch(log, /Unhandled exception/);
    assert.match(log, /data folder.*(cannot|can't|not) (be )?writ/i);
  } finally {
    docker("rm", "--force", name);
    docker("volume", "rm", volume);
  }
});

test("DOK-10 and DAT-10: after docker stop every database is checkpointed, so no -wal file keeps data the .db file does not hold", {
  skip: "DEFECT (DOK-10, DAT-10): after a clean docker stop (exit 0 in under a second) cache.db, jobs.db, movies.db and series.db have no -wal file, but platform.db is 4096 bytes with a 2.8 MB platform.db-wal beside it. Reproduced on two runs. Copying platform.db alone (a file-level backup, a volume snapshot of one file) holds none of the data."
}, () => {
  const name = `deluno-defect-dat10-${process.pid}`;
  const volume = `${name}-data`;
  try {
    docker("volume", "create", volume);
    docker("run", "--detach", "--name", name, "--mount", `type=volume,src=${volume},dst=/data`, image);
    assert.ok(waitFor(() => docker("exec", name, "curl", "--fail", "--silent", "http://127.0.0.1:8080/api/health/ready").status === 0, 90));
    assert.equal(docker("stop", "--time", "30", name).status, 0);
    const listing = docker("run", "--rm", "--entrypoint", "sh", "--mount", `type=volume,src=${volume},dst=/data`, image, "-c", "ls -l /data/*.db-wal 2>/dev/null; true").out;
    const left = listing.split("\n").filter((line) => line && !/\s0 \w{3}\s+\d+\s+[\d:]+ /.test(line));
    assert.deepEqual(left, [], "a -wal file with content is left behind");
  } finally {
    docker("rm", "--force", name);
    docker("volume", "rm", volume);
  }
});

test("DOK-07: the container's HEALTHCHECK asks whether Deluno is ready, so a broken database or a full data folder reads unhealthy", {
  skip: "DEFECT (DOK-07): the Dockerfile and compose.yaml HEALTHCHECK call /health, which answers 200 while the process serves. With a full data folder /api/health/ready answers 503 ('storage:writable ... No space left on device') while docker inspect keeps saying healthy."
}, () => {
  for (const file of ["Dockerfile", "compose.yaml"]) {
    const text = read(file);
    assert.match(text, /\/api\/health\/ready/, `${file} health check calls the readiness endpoint`);
  }
});

test("DAT-11: a write that fails because the data folder is full tells the person the data folder is full", {
  skip: "DEFECT (DAT-11): with /data full (tmpfs of 40 MB filled), POST /api/movies answers 500 with only 'An unexpected error occurred.', and the Health screen lists nothing. Only /api/health/ready says why ('storage:writable: Storage root is not writable: No space left on device'). Deluno stays up, which is right."
}, () => {
  const name = `deluno-defect-dat11-${process.pid}`;
  try {
    docker("run", "--detach", "--name", name, "--mount", "type=tmpfs,dst=/data,tmpfs-size=40m,tmpfs-mode=1777", image);
    assert.ok(waitFor(() => docker("exec", name, "curl", "--fail", "--silent", "http://127.0.0.1:8080/api/health/ready").status === 0, 90));
    docker("exec", name, "sh", "-c", "dd if=/dev/zero of=/data/filler bs=1M >/dev/null 2>&1");
    const reply = docker("exec", name, "curl", "--silent", "--header", "Content-Type: application/json", "--data", '{"title":"Full","releaseYear":2026,"monitored":false}', "http://127.0.0.1:8080/api/movies").out;
    assert.match(reply, /(full|space)/i);
  } finally {
    docker("rm", "--force", name);
  }
});

test("DOK-18: the release builds the image for arm64 as well as amd64", {
  skip: "GAP (DOK-18): release.yml and ci.yml build platforms: linux/amd64 only, so a Synology, Raspberry Pi or Apple-silicon Docker host gets an emulated or missing image."
}, () => {
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
