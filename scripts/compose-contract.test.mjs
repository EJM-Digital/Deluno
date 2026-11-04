// node --test scripts/compose-contract.test.mjs
// The compose file's contract, read from what Docker itself makes of it (`docker compose config --format json`), not from its text:
// host 5099 to the container's 8080, the /data volume, the restart policy and the healthcheck, and that the settings the app
// reads (port, LAN access, data root, master key, image ref) reach the container. Runs in the "Container image" job of ci.yml.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { test } from "node:test";

function composeConfig(extraEnv = {}) {
  const env = { ...process.env };
  delete env.DELUNO_IMAGE;
  delete env.DELUNO_DATA_DIR;
  delete env.DELUNO_MASTER_KEY;
  Object.assign(env, extraEnv);
  const result = spawnSync("docker", ["compose", "-f", "compose.yaml", "config", "--format", "json"], { encoding: "utf8", env });
  assert.equal(result.status, 0, `docker compose config failed: ${result.stderr}`);
  return JSON.parse(result.stdout).services.deluno;
}

test("the app is published on host port 5099, to the container's 8080", () => {
  const service = composeConfig();
  const published = service.ports.find((port) => Number(port.target) === 8080);
  assert.ok(published, "container port 8080 is published");
  assert.equal(String(published.published), "5099");
  assert.equal(published.protocol ?? "tcp", "tcp");
});

test("the /data volume holds the app's data and the folder it comes from is a setting", () => {
  const service = composeConfig();
  const data = service.volumes.find((volume) => volume.target === "/data");
  assert.ok(data, "/data is mounted");
  assert.equal(data.type, "bind");
  assert.match(data.source.replaceAll("\\", "/"), /artifacts\/docker\/data$/, "the default folder");

  const moved = composeConfig({ DELUNO_DATA_DIR: "/srv/deluno-data" }).volumes.find((volume) => volume.target === "/data");
  assert.equal(moved.source, "/srv/deluno-data", "DELUNO_DATA_DIR moves it");
});

test("the container restarts unless it was stopped on purpose", () => {
  assert.equal(composeConfig().restart, "unless-stopped");
});

test("the healthcheck asks the app whether it is ready, and gives the first start time", () => {
  const { healthcheck } = composeConfig();
  assert.deepEqual(healthcheck.test.slice(0, 2), ["CMD", "curl"]);
  assert.ok(healthcheck.test.some((part) => String(part).endsWith("/api/health/ready")), "it asks /api/health/ready");
  assert.ok(healthcheck.test.some((part) => String(part).includes(":8080")), "on the container's own port");
  assert.ok(Number(healthcheck.retries) >= 3);
  assert.notEqual(healthcheck.start_period, undefined, "a start period is set, so a slow first start is not an unhealthy one");
});

test("the settings the app reads reach the container: port, LAN access, data root, master key and image ref", () => {
  const environment = composeConfig({ DELUNO_MASTER_KEY: "compose-contract-key", DELUNO_IMAGE: "ghcr.io/example/deluno:1.2.3" }).environment;
  assert.equal(environment.Server__Port, "8080");
  assert.equal(environment.Server__AllowLan, "true");
  assert.equal(environment.Storage__DataRoot, "/data");
  assert.equal(environment.ASPNETCORE_URLS, "http://+:8080");
  assert.equal(environment.DELUNO_MASTER_KEY, "compose-contract-key");
  assert.equal(environment.DELUNO_IMAGE_REF, "ghcr.io/example/deluno:1.2.3");
});
