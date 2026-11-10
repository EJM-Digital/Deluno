// node --test scripts/ci-passed.test.mjs
// CI's single verdict on hand-built `needs` objects, the shape GitHub's toJSON(needs) produces.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

import { GATES, NEVER_SELF_HOSTED, verdict } from "./ci-passed.mjs";

function needs({ due = {}, results = {}, changes = "success" } = {}) {
  const outputs = {};
  for (const flag of new Set(Object.values(GATES).filter(Boolean))) outputs[flag] = due[flag] === false ? "false" : "true";
  const all = { changes: { result: changes, outputs } };
  for (const [job, flag] of Object.entries(GATES)) {
    all[job] = { result: results[job] ?? (flag && due[flag] === false ? "skipped" : "success") };
  }
  return all;
}

test("everything due and passed", () => {
  assert.deepEqual(verdict(needs()), []);
});

test("a job skipped because its paths did not change is a pass", () => {
  assert.deepEqual(verdict(needs({ due: { run_backend: false, run_docker: false } })), []);
});

test("a due job that failed, was cancelled or was skipped fails the verdict", () => {
  for (const result of ["failure", "cancelled", "skipped"]) {
    const problems = verdict(needs({ results: { backend: result } }));
    assert.equal(problems.length, 1);
    assert.match(problems[0], /backend ended .* was due/);
  }
});

test("a job that ran when it was not due means the flags and conditions disagree", () => {
  const problems = verdict(needs({ due: { run_docker: false }, results: { docker: "success" } }));
  assert.match(problems.join("\n"), /docker ended success; it was not due/);
});

test("a job that always runs must pass", () => {
  assert.match(verdict(needs({ results: { "repo-checks": "skipped" } })).join("\n"), /repo-checks ended skipped/);
});

test("failed change detection fails the verdict outright", () => {
  const problems = verdict(needs({ changes: "failure" }));
  assert.equal(problems.length, 1);
  assert.match(problems[0], /changes ended failure/);
});

test("a missing flag, a missing job or an unknown job is refused", () => {
  const noFlag = needs();
  delete noFlag.changes.outputs.run_web;
  assert.match(verdict(noFlag).join("\n"), /no usable run_web/);

  const missing = needs();
  delete missing.docker;
  assert.match(verdict(missing).join("\n"), /docker is not in ci-passed's needs/);

  const extra = { ...needs(), "new-job": { result: "success" } };
  assert.match(verdict(extra).join("\n"), /new-job is not in scripts\/ci-passed.mjs GATES/);
});

test("the Windows end-to-end job is due exactly when the installer job is", () => {
  assert.equal(GATES["e2e-windows"], GATES["windows-installer"]);
  assert.deepEqual(verdict(needs({ due: { run_windows_installer: false } })), []);
  // Due, but skipped (the installer failed, so its dependent never ran): not a pass.
  const problems = verdict(needs({ results: { "e2e-windows": "skipped" } }));
  assert.equal(problems.length, 1);
  assert.match(problems[0], /e2e-windows ended skipped; it was due/);
  // Not due, but it ran: the flags and the job condition disagree.
  assert.match(verdict(needs({ due: { run_windows_installer: false }, results: { "e2e-windows": "success" } })).join(" "), /e2e-windows ended success; it was not due/);
});

test("with the Windows jobs on a self-hosted runner, the Windows end-to-end job must be skipped, never run", () => {
  const options = { windowsSelfHosted: true };
  assert.deepEqual(verdict(needs({ results: { "e2e-windows": "skipped" } }), options), []);
  assert.match(verdict(needs({ results: { "e2e-windows": "success" } }), options).join(" "), /e2e-windows ended success; it was not due/);
  assert.match(verdict(needs({ results: { "e2e-windows": "failure" } }), options).join(" "), /e2e-windows ended failure/);
  // Only that job is waived: a skipped installer is still a failure when it was due.
  assert.match(verdict(needs({ results: { "windows-installer": "skipped", "e2e-windows": "skipped" } }), options).join(" "), /windows-installer ended skipped; it was due/);
  // Without the variable nothing changes.
  assert.match(verdict(needs({ results: { "e2e-windows": "skipped" } })).join(" "), /e2e-windows ended skipped; it was due/);
});

test("a missing changes job and an empty flag set are reported, not thrown (and a changes job with no outputs published no flags)", () => {
  assert.match(verdict({})[0], /changes ended missing/);
  const noFlags = needs();
  delete noFlags.changes.outputs;
  assert.match(verdict(noFlags).join(" "), /changes published no usable run_backend/);
});

// The job itself: the script run as GitHub runs it, with toJSON(needs) in NEEDS.
function runVerdict(needsObject, extraEnv = {}) {
  const result = spawnSync(process.execPath, [fileURLToPath(new URL("./ci-passed.mjs", import.meta.url))], {
    encoding: "utf8",
    env: { ...process.env, NEEDS: JSON.stringify(needsObject), DELUNO_WINDOWS_RUNS_ON: "", ...extraEnv }
  });
  return { status: result.status, out: result.stdout, err: result.stderr };
}

test("run as the ci-passed job, it prints each job's state and exits 0 when every due job passed", () => {
  const run = runVerdict(needs());
  assert.equal(run.status, 0, run.err);
  assert.match(run.out, /repo-checks\s+always\s+success/);
  assert.match(run.out, /Every job that was due passed/);
});

test("run as the ci-passed job, a failed or missing job exits 1 with an error line for each", () => {
  const all = needs({ results: { backend: "failure" } });
  delete all.web;
  const run = runVerdict(all);
  assert.equal(run.status, 1);
  assert.match(run.err, /::error::backend ended failure/);
  assert.match(run.err, /::error::web is not in ci-passed's needs/);
  assert.match(run.out, /web\s+(due|not due)\s+missing/);
});

test("run as the ci-passed job on a self-hosted Windows runner, the installer end-to-end job must be skipped", () => {
  const all = needs({ results: { [NEVER_SELF_HOSTED]: "skipped" } });
  assert.equal(runVerdict(all, { DELUNO_WINDOWS_RUNS_ON: '"self-hosted"' }).status, 0);
  assert.equal(runVerdict(all).status, 1);
});
