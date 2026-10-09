// node --test scripts/tag-release.test.mjs
// Each way scripts/tag-release.ps1 refuses to tag, on hand-built values.
import assert from "node:assert/strict";
import { test } from "node:test";

import {
  checkCi, checkGoldenPath, checkPropsVersion, checkPushed, checkReleaseNotes, checkVersionFormat, checkWorkingTree,
} from "./tag-release-checks.mjs";

const sha = "a".repeat(40);

test("only release versions are accepted", () => {
  for (const ok of ["1.0.0", "1.0.0-rc.30", "1.0.0-rc.30.15"]) assert.deepEqual(checkVersionFormat(ok), []);
  for (const bad of ["", "v1.0.0", "1.0", "1.0.0-beta", "1.0.0-rc."]) assert.equal(checkVersionFormat(bad).length, 1, bad);
});

test("a dirty working tree is refused", () => {
  assert.deepEqual(checkWorkingTree(""), []);
  assert.match(checkWorkingTree(" M scripts/gate.ps1\n")[0], /not committed/);
});

test("HEAD must equal origin/main", () => {
  assert.deepEqual(checkPushed(sha, sha), []);
  assert.match(checkPushed(sha, "b".repeat(40))[0], /not the same commit as origin\/main/);
  assert.match(checkPushed(sha, "")[0], /git fetch/);
});

test("Directory.Build.props must carry the version", () => {
  const props = "<Project><PropertyGroup><Version>1.0.0-rc.30.14</Version></PropertyGroup></Project>";
  assert.deepEqual(checkPropsVersion(props, "1.0.0-rc.30.14"), []);
  assert.match(checkPropsVersion(props, "1.0.0-rc.30.15")[0], /says version 1.0.0-rc.30.14, not 1.0.0-rc.30.15/);
  assert.match(checkPropsVersion("<Project/>", "1.0.0")[0], /no <Version>/);
});

test("RELEASE_NOTES.md must have the version's section", () => {
  const notes = "# Notes\n\n## 1.0.0-rc.30.14\n\ntext\n\n## 1.0.0-rc.30\n";
  assert.deepEqual(checkReleaseNotes(notes, "1.0.0-rc.30.14"), []);
  assert.match(checkReleaseNotes(notes, "1.0.0-rc.30.15")[0], /no "## 1.0.0-rc.30.15" section/);
  assert.equal(checkReleaseNotes("## 1.0.0-rc.30.145\n", "1.0.0-rc.30.14").length, 1);
});

const run = (overrides = {}) => ({ databaseId: 7, event: "push", status: "completed", conclusion: "success", ...overrides });
const job = (name, conclusion = "success", steps = []) => ({ name, conclusion, steps });
const fullJobs = [
  job("ci-passed", "success", [{ name: "Every job that was due passed", conclusion: "success" }]),
  job("Frontend smoke (1/2)"),
  job("Frontend smoke (2/2)"),
];

test("a green run that ran the smoke shards qualifies", () => {
  assert.deepEqual(checkCi([run()], () => fullJobs), []);
});

test("no run, a failed run, an unfinished run and a pull request run are refused", () => {
  assert.match(checkCi([], () => [])[0], /No push or manual run/);
  assert.match(checkCi([run({ event: "pull_request" })], () => fullJobs)[0], /No push or manual run/);
  assert.match(checkCi([run({ conclusion: "failure" })], () => [])[0], /concluded failure/);
  assert.match(checkCi([run({ status: "in_progress", conclusion: null })], () => [])[0], /still in_progress/);
});

test("a green documents-only run, which skipped the smoke jobs, is refused", () => {
  const docsOnly = [fullJobs[0], job("Frontend smoke (1/2)", "skipped"), job("Frontend smoke (2/2)", "skipped")];
  assert.match(checkCi([run()], () => docsOnly)[0], /did not run the jobs/);
  assert.match(checkCi([run()], () => [fullJobs[0]])[0], /did not run the jobs/);
});

test("an earlier failed run does not hide a later green full run", () => {
  assert.deepEqual(checkCi([run({ databaseId: 1, conclusion: "failure" }), run({ databaseId: 2 })], () => fullJobs), []);
});

test("the golden path must be success, newest status first", () => {
  const status = (id, state, at) => ({ id, context: "golden-path", state, created_at: at });
  assert.deepEqual(checkGoldenPath([status(1, "success", "2026-10-01T00:00:00Z")]), []);
  assert.match(checkGoldenPath([])[0], /no status/);
  assert.match(
    checkGoldenPath([status(1, "success", "2026-10-01T00:00:00Z"), status(2, "failure", "2026-10-02T00:00:00Z")])[0],
    /is failure/,
  );
});
