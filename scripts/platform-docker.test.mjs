// node --test scripts/platform-docker.test.mjs
// The platform defect tests that start the Deluno image (DOK-08, DOK-10 and DAT-10, DAT-11, AUT-20, DOK-16), split out of
// platform-defects.test.mjs so the job that builds the image ("Container image" in ci.yml) runs them, and no job without an
// image does. The image is named by DELUNO_DOCKER_IMAGE (Linux or WSL with docker). Container, volume and port names carry
// a random suffix as well as the process id, so two jobs sharing one Docker daemon never collide.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { randomBytes } from "node:crypto";
import { test } from "node:test";

const image = process.env.DELUNO_DOCKER_IMAGE ?? "deluno:dev";
const unique = `${process.pid}-${randomBytes(3).toString("hex")}`;

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

test("DOK-08: a data folder Deluno cannot write ends with a plain sentence saying so, not an unhandled exception and exit code 139", () => {
  const name = `deluno-defect-dok08-${unique}`;
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

test("DOK-10 and DAT-10: after docker stop every database is checkpointed, so no -wal file keeps data the .db file does not hold", () => {
  const name = `deluno-defect-dat10-${unique}`;
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

test("DAT-11: a write that fails because the data folder is full tells the person the data folder is full", () => {
  const name = `deluno-defect-dat11-${unique}`;
  try {
    docker("run", "--detach", "--name", name, "--mount", "type=tmpfs,dst=/data,tmpfs-size=40m,tmpfs-mode=1777", image);
    assert.ok(waitFor(() => docker("exec", name, "curl", "--fail", "--silent", "http://127.0.0.1:8080/api/health/ready").status === 0, 90));
    // Sign in while there is still room, then fill the folder, then write.
    const signedIn = docker("exec", name, "curl", "--silent", "--header", "Content-Type: application/json", "--data",
      '{"username":"owner","displayName":"Owner","password":"owner-password-1"}', "http://127.0.0.1:8080/api/auth/bootstrap").out;
    const token = /"accessToken":"([^"]+)"/.exec(signedIn)?.[1];
    assert.ok(token, `signed in: ${signedIn}`);
    docker("exec", name, "sh", "-c", "dd if=/dev/zero of=/data/filler bs=1M >/dev/null 2>&1");
    const reply = docker("exec", name, "curl", "--silent", "--header", `Authorization: Bearer ${token}`, "--header", "Content-Type: application/json", "--data", '{"title":"Full","releaseYear":2026,"monitored":false}', "http://127.0.0.1:8080/api/movies").out;
    assert.match(reply, /(full|space)/i);
  } finally {
    docker("rm", "--force", name);
  }
});

test("AUT-20: a backup file alone does not unlock the saved passwords: it carries neither the master key nor the keys that open them", {
  skip: "DEFECT (AUT-20): a backup made by a container without DELUNO_MASTER_KEY lists data/secrets/master.key and data/protection-keys/*.xml beside the databases that hold the encrypted indexer and client passwords, so anyone holding the zip can read every saved password. With DELUNO_MASTER_KEY supplied the key is not in the zip, but protection-keys still are."
}, async () => {
  const name = `deluno-defect-aut20-${unique}`;
  try {
    docker("run", "--detach", "--name", name, "--publish", "127.0.0.1::8080", image);
    const port = /:([0-9]+)$/m.exec(docker("port", name, "8080/tcp").out)?.[1];
    assert.ok(port, "the container publishes a port");
    assert.ok(waitFor(() => docker("exec", name, "curl", "--fail", "--silent", "http://127.0.0.1:8080/api/health/ready").status === 0, 90));
    const base = `http://127.0.0.1:${port}`;
    const json = { "Content-Type": "application/json" };
    const signedIn = await (await fetch(`${base}/api/auth/bootstrap`, { method: "POST", headers: json, body: JSON.stringify({ username: "owner", displayName: "Owner", password: "owner-password-1" }) })).json();
    const auth = { Authorization: `Bearer ${signedIn.accessToken}` };
    const made = await (await fetch(`${base}/api/backups`, { method: "POST", headers: { ...json, ...auth }, body: JSON.stringify({ reason: "manual" }) })).json();
    const zip = Buffer.from(await (await fetch(`${base}/api/backups/${made.backup.id}/download`, { headers: auth })).arrayBuffer());
    assert.ok(!zip.includes("secrets/master.key"), "the backup carries the master key");
    assert.ok(!zip.includes("protection-keys/"), "the backup carries the data-protection keys");
  } finally {
    docker("rm", "--force", name);
  }
});

test("DOK-16: an older image started on a volume a newer image has migrated refuses to run, with a plain message, or leaves the data untouched", () => {
  // An older image cannot be built from this commit, so the newer volume is made the other way round: the volume's platform
  // database is stamped with a change number this image has never heard of, as a later release would have left it.
  const volume = `deluno-defect-dok16-${unique}-data`;
  const names = [`deluno-defect-dok16-${unique}-new`, `deluno-defect-dok16-${unique}-old`];
  try {
    docker("volume", "create", volume);
    docker("run", "--detach", "--name", names[0], "--mount", `type=volume,src=${volume},dst=/data`, image);
    assert.ok(waitFor(() => docker("exec", names[0], "curl", "--fail", "--silent", "http://127.0.0.1:8080/api/health/ready").status === 0, 90));
    assert.equal(docker("stop", "--time", "30", names[0]).status, 0);
    const stamped = docker("run", "--rm", "--entrypoint", "python3", "--mount", `type=volume,src=${volume},dst=/data`, "python:3.13-alpine", "-c",
      "import sqlite3;c=sqlite3.connect('/data/platform.db');c.execute(\"insert into schema_migrations(version,name,checksum,applied_utc) values (99999,'FromANewerRelease','x','2099-01-01T00:00:00Z')\");c.commit();c.close()");
    assert.equal(stamped.status, 0, stamped.out);
    docker("run", "--detach", "--name", names[1], "--mount", `type=volume,src=${volume},dst=/data`, image);
    assert.ok(waitFor(() => docker("inspect", "--format", "{{.State.Status}}", names[1]).out === "exited", 60), "the image should refuse to run on a newer volume");
    const log = docker("logs", names[1]).out;
    assert.match(log, /newer (version|build) of Deluno/i);
    assert.doesNotMatch(log, /Unhandled exception/);
    assert.notEqual(docker("inspect", "--format", "{{.State.ExitCode}}", names[1]).out, "0");
  } finally {
    for (const name of names) docker("rm", "--force", name);
    docker("volume", "rm", volume);
  }
});
