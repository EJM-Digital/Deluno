import { test } from "node:test";
import assert from "node:assert/strict";
import { score, survivors, compare, update, validateThresholds, validateEquivalents, unlistedSurvivors, floor2 } from "./check-mutation-ratchet.mjs";
import { plan } from "./mutation/run-changed.mjs";

const m = (id, status, line = 1, mutator = "Arithmetic", replacement = "-") => ({ id, status, mutatorName: mutator, replacement, location: { start: { line, column: 1 }, end: { line, column: 2 } } });
const report = (files) => ({ schemaVersion: "1", files: Object.fromEntries(Object.entries(files).map(([f, ms]) => [f, { language: "cs", source: "", mutants: ms }])) });

test("the score counts killed and timed-out mutants over every one that counts, and ignores compile errors", () => {
  const r = report({ "A.cs": [m("1", "Killed"), m("2", "Timeout"), m("3", "Survived"), m("4", "NoCoverage"), m("5", "CompileError"), m("6", "Ignored")] });
  assert.equal(score(r), 50);
  assert.equal(score(report({ "A.cs": [m("1", "Ignored")] })), null);
  assert.equal(survivors(r).length, 2);
});

test("a score below the threshold fails; at it or above passes; an unknown project is a note", () => {
  const t = { projects: { A: { score: 80 } } };
  assert.equal(compare(t, { A: 79.99 }).failures.length, 1);
  assert.deepEqual(compare(t, { A: 80 }).failures, []);
  assert.deepEqual(compare(t, { A: 100 }).failures, []);
  assert.equal(compare(t, { B: 10 }).notes.length, 1);
  assert.equal(compare(t, { A: null }).notes.length, 1);
});

test("update raises a threshold, never lowers it, and rounds down", () => {
  const t = { projects: { A: { score: 80 }, B: { score: 10 } } };
  const next = update(t, { A: 70, B: 66.6666, C: 12.5, D: null });
  assert.deepEqual(next.projects, { A: { score: 80 }, B: { score: 66.66 }, C: { score: 12.5 } });
  assert.equal(floor2(99.999), 99.99);
});

test("a survivor fails unless the equivalent list names its file, mutator, line and replacement", () => {
  const r = report({ "Foo/Bar.cs": [m("1", "Survived", 12, "Equality", "!="), m("2", "Survived", 20), m("3", "Killed", 30)] });
  const eq = { equivalents: [{ project: "P", file: "Foo/Bar.cs", mutator: "Equality", line: 12, replacement: "!=", reason: "The loop always ends exactly at the length." }] };
  const open = unlistedSurvivors(eq, "P", r);
  assert.deepEqual(open.map((x) => x.line), [20]);
  // Another project's list does not cover this one.
  assert.equal(unlistedSurvivors(eq, "Q", r).length, 2);
  // The line moving makes the entry stale, so the survivor reappears.
  assert.equal(unlistedSurvivors({ equivalents: [{ ...eq.equivalents[0], line: 13 }] }, "P", r).length, 2);
});

test("an equivalent mutant without a reason, a line or a mutator is refused", () => {
  const good = { project: "P", file: "A.cs", mutator: "Equality", line: 3, reason: "No test can tell the two apart here." };
  assert.deepEqual(validateEquivalents({ equivalents: [good] }), []);
  assert.equal(validateEquivalents({ equivalents: [{ ...good, reason: "todo" }] }).length, 1);
  assert.equal(validateEquivalents({ equivalents: [{ ...good, line: undefined }] }).length, 1);
  assert.equal(validateEquivalents({ equivalents: [{ ...good, mutator: "" }] }).length, 1);
  assert.equal(validateEquivalents({}).length, 1);
});

test("thresholds must be numbers from 0 to 100", () => {
  assert.deepEqual(validateThresholds({ projects: { A: { score: 0 } } }), []);
  assert.equal(validateThresholds({ projects: { A: { score: 101 } } }).length, 1);
  assert.equal(validateThresholds({ projects: { A: {} } }).length, 1);
  assert.equal(validateThresholds({}).length, 1);
});

test("the changed-file plan picks the .NET projects and the JavaScript source files, not tests or migrations", () => {
  const p = plan([
    "src/Deluno.Contracts/Foo.cs",
    "src/Deluno.Contracts/Sub/Bar.cs",
    "src/Deluno.Movies/Migrations/V0001.cs",
    "src/Deluno.Jobs/obj/x.cs",
    "tests/Deluno.Movies.Tests/A.cs",
    "apps/web/src/lib/a.ts",
    "apps/web/src/lib/a.test.ts",
    "apps/web/src/test/setup.ts",
    "apps/web/src/pages/P.tsx",
    "services/metadata-gateway/src/cache.js",
    "docs/x.md"
  ]);
  assert.deepEqual(p.dotnet, ["Deluno.Contracts"]);
  assert.deepEqual(p.js.map((j) => [j.name, j.files]), [
    ["web", ["src/lib/a.ts", "src/pages/P.tsx"]],
    ["metadata-gateway", ["src/cache.js"]]
  ]);
  assert.deepEqual(plan(["docs/a.md"]), { dotnet: [], js: [] });
});
