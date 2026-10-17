// node --test scripts/verify-golden-path-for-release.test.mjs
// The release gate's judgement of a commit's statuses, on hand-built status objects.
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { test } from "node:test";

import { NO_PASSING_RUN, evaluateStatuses, findGoldenPathPass, latestStatus } from "./verify-golden-path-for-release.mjs";

let nextId = 1;
const status = (overrides = {}) => ({
  id: nextId++,
  context: "golden-path",
  state: "success",
  description: "Passed on the clean VM",
  created_at: "2026-10-07T10:00:00Z",
  ...overrides,
});

test("a success for the golden-path context passes", () => {
  assert.deepEqual(evaluateStatuses([status()]), []);
});

test("a commit with no statuses, or none for the context, does not pass", () => {
  assert.match(evaluateStatuses([]).join("\n"), /no status with the context "golden-path"/);
  assert.match(evaluateStatuses([status({ context: "ci-passed" })]).join("\n"), /no status with the context "golden-path"/);
});

test("a success under another context does not count", () => {
  assert.notDeepEqual(evaluateStatuses([status({ context: "golden-path-draft" }), status({ context: "ci-passed" })]), []);
});

test("failure, error and pending do not pass", () => {
  for (const state of ["failure", "error", "pending"]) {
    assert.match(evaluateStatuses([status({ state })]).join("\n"), new RegExp(`latest "golden-path" status is ${state}`));
  }
});

test("the failure's description is part of the reason", () => {
  assert.match(evaluateStatuses([status({ state: "failure", description: "Pause did not hold" })]).join("\n"), /Pause did not hold/);
});

test("the newest status for the context counts, whatever order the API lists them in", () => {
  const passed = status({ created_at: "2026-10-07T10:00:00Z" });
  const failedLater = status({ state: "failure", created_at: "2026-10-07T11:00:00Z" });
  assert.notDeepEqual(evaluateStatuses([failedLater, passed]), []);
  assert.notDeepEqual(evaluateStatuses([passed, failedLater]), []);

  const failedFirst = status({ state: "failure", created_at: "2026-10-07T09:00:00Z" });
  const passedLater = status({ created_at: "2026-10-07T12:00:00Z" });
  assert.deepEqual(evaluateStatuses([passedLater, failedFirst]), []);
  assert.deepEqual(evaluateStatuses([failedFirst, passedLater]), []);
});

test("two statuses made in the same second are told apart by id", () => {
  const first = status({ state: "failure" });
  const second = status();
  assert.equal(latestStatus([second, first]).id, second.id);
  assert.equal(latestStatus([first, second]).id, second.id);
  assert.deepEqual(evaluateStatuses([first, second]), []);
});

test("other contexts never hide the golden-path status", () => {
  const passed = status();
  const newerOther = status({ context: "ci-passed", state: "failure", created_at: "2026-10-07T13:00:00Z" });
  assert.deepEqual(evaluateStatuses([newerOther, passed]), []);
});

test("the refusal tells the operator what to do", () => {
  assert.equal(
    NO_PASSING_RUN,
    "This commit has no passing golden-path run. Run the golden path on this exact build (see docs/golden-path.md), then re-run the release.",
  );
});

// The carry-over rule (James, 16 Oct 2026): a pass on an ancestor counts when no file that ships changed since.
// Run on a real throwaway git repository, so the history is real.
function repo() {
  const dir = mkdtempSync(join(tmpdir(), "carry-"));
  const git = (args) => execFileSync(
    "git",
    ["-C", dir, "-c", "user.name=t", "-c", "user.email=t@example.com", "-c", "commit.gpgsign=false", ...args],
    { encoding: "utf8" },
  );
  git(["init", "-q", "-b", "main"]);
  const commit = (files) => {
    for (const [path, text] of Object.entries(files)) {
      mkdirSync(dirname(join(dir, path)), { recursive: true });
      writeFileSync(join(dir, path), text);
    }
    git(["add", "-A"]);
    git(["commit", "-q", "-m", "change"]);
    return git(["rev-parse", "HEAD"]).trim();
  };
  return { git, commit, done: () => rmSync(dir, { recursive: true, force: true }) };
}

const props = (version) => `<Project><PropertyGroup><Version>${version}</Version></PropertyGroup></Project>\n`;
const passOn = (...shas) => (sha) => (shas.includes(sha) ? [status()] : []);

test("a pass on the same commit counts", () => {
  const r = repo();
  try {
    const x = r.commit({ "src/a.cs": "a", "Directory.Build.props": props("1.0.0-rc.1") });
    const found = findGoldenPathPass(x, { listStatuses: passOn(x), git: r.git });
    assert.equal(found.ok, true);
    assert.equal(found.carried, false);
    assert.equal(found.sha, x);
  } finally { r.done(); }
});

test("a pass on an ancestor counts when only tests, scripts, docs or CI files changed", () => {
  const r = repo();
  try {
    const x = r.commit({ "src/a.cs": "a", "Directory.Build.props": props("1.0.0-rc.1") });
    r.commit({ "tests/A.Tests/ATests.cs": "t", "docs/x.md": "d", ".github/workflows/ci.yml": "ci" });
    r.commit({ "scripts/check-something.mjs": "s", "apps/web/src/foo.test.ts": "t", "apps/web/tests/e2e/a.spec.ts": "t" });
    const y = r.commit({ "scripts/golden-path/Invoke-ScenarioSuite.ps1": "s" });
    const found = findGoldenPathPass(y, { listStatuses: passOn(x), git: r.git });
    assert.equal(found.ok, true);
    assert.equal(found.carried, true);
    assert.equal(found.sha, x);
  } finally { r.done(); }
});

test("a pass on an ancestor is refused when a file that ships changed after it", () => {
  const changes = [{ "src/a.cs": "changed" }, { "apps/web/src/page.tsx": "p" }, { "RELEASE_NOTES.md": "n" }, { Dockerfile: "d" }, { "installer/deluno.ico": "i" }];
  for (const shipped of changes) {
    const r = repo();
    try {
      const x = r.commit({ "src/a.cs": "a", "Directory.Build.props": props("1.0.0-rc.1") });
      r.commit(shipped);
      const y = r.commit({ "docs/x.md": "d" });
      const found = findGoldenPathPass(y, { listStatuses: passOn(x), git: r.git });
      assert.equal(found.ok, false, JSON.stringify(shipped));
      assert.match(found.problems.join("\n"), /same shipped files/);
    } finally { r.done(); }
  }
});

test("a different version is a different product: a version-only change is refused", () => {
  const r = repo();
  try {
    const x = r.commit({ "src/a.cs": "a", "Directory.Build.props": props("1.0.0-rc.1") });
    const y = r.commit({ "Directory.Build.props": props("1.0.0-rc.2") });
    const found = findGoldenPathPass(y, { listStatuses: passOn(x), git: r.git });
    assert.equal(found.ok, false);
    assert.match(found.problems.join("\n"), /Directory\.Build\.props/);
  } finally { r.done(); }
});

test("a pass on a commit that is not an ancestor is never used", () => {
  const r = repo();
  try {
    r.commit({ "src/a.cs": "a" });
    r.git(["checkout", "-q", "-b", "side"]);
    const side = r.commit({ "docs/side.md": "s" });
    r.git(["checkout", "-q", "main"]);
    const y = r.commit({ "docs/main.md": "m" });
    const found = findGoldenPathPass(y, { listStatuses: passOn(side), git: r.git });
    assert.equal(found.ok, false);
  } finally { r.done(); }
});

test("a later failure on identical shipped files is not skipped in favour of an older pass", () => {
  const r = repo();
  try {
    const x = r.commit({ "src/a.cs": "a" });
    const mid = r.commit({ "docs/x.md": "d" });
    const y = r.commit({ "docs/y.md": "d" });
    const statuses = (sha) => (sha === x ? [status()] : sha === mid ? [status({ state: "failure", description: "Pause did not hold" })] : []);
    const found = findGoldenPathPass(y, { listStatuses: statuses, git: r.git });
    assert.equal(found.ok, false);
    assert.match(found.problems.join("\n"), /Pause did not hold/);
  } finally { r.done(); }
});

test("a failure recorded on the tagged commit itself is the answer", () => {
  const r = repo();
  try {
    const x = r.commit({ "src/a.cs": "a" });
    const y = r.commit({ "docs/y.md": "d" });
    const statuses = (sha) => (sha === x ? [status()] : sha === y ? [status({ state: "failure" })] : []);
    assert.equal(findGoldenPathPass(y, { listStatuses: statuses, git: r.git }).ok, false);
  } finally { r.done(); }
});

test("history that was not fetched is refused, not guessed", () => {
  const failing = () => { throw new Error("fatal: bad object"); };
  const found = findGoldenPathPass("a".repeat(40), { listStatuses: () => [], git: failing });
  assert.equal(found.ok, false);
  assert.match(found.problems.join("\n"), /full history/);
});
