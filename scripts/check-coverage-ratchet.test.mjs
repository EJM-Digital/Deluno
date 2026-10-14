import { test } from "node:test";
import assert from "node:assert/strict";
import { compare, update, validateExclusions, validateReportExclusions, floor2 } from "./check-coverage-ratchet.mjs";

const file = (lc, lt, bc, bt) => ({ lines: { covered: lc, total: lt }, branches: { covered: bc, total: bt } });

test("an exclusion without a reason fails, one with a reason passes", () => {
  assert.deepEqual(validateExclusions({ exclusions: [{ path: "src/A.cs", reason: "Process entry point glue only." }] }), []);
  assert.equal(validateExclusions({ exclusions: [{ path: "src/A.cs" }] }).length, 1);
  assert.equal(validateExclusions({ exclusions: [{ path: "src/A.cs", reason: "  " }] }).length, 1);
  assert.equal(validateExclusions({ exclusions: [{ path: "src/A.cs", reason: "todo" }] }).length, 1);
  assert.equal(validateExclusions({ exclusions: [{ reason: "A reason that is long enough." }] }).length, 1);
  assert.equal(validateExclusions({}).length, 1);
});

test("an excluded file in a report with no reason fails", () => {
  assert.equal(validateReportExclusions({ excluded: [{ file: "src/A.cs", reason: "" }] }).length, 1);
  assert.deepEqual(validateReportExclusions({ excluded: [{ file: "src/A.cs", reason: "Because." }] }), []);
});

test("rounds down so a file never fails against its own measurement", () => {
  assert.equal(floor2(66.6666), 66.66);
  assert.equal(floor2(100), 100);
  assert.equal(floor2(null), null);
});

test("an empty baseline holds nothing back", () => {
  const r = compare({ files: {} }, { files: { "src/A.cs": file(1, 10, 0, 4) } });
  assert.deepEqual(r.failures, []);
  assert.equal(r.notes.length >= 1, true);
});

test("a file whose line or branch coverage drops below its baseline fails", () => {
  const baseline = { files: { "src/A.cs": { line: 80, branch: 50 }, "src/B.cs": { line: 100, branch: null } } };
  const report = { files: { "src/A.cs": file(79, 100, 1, 4), "src/B.cs": file(10, 10, 0, 0) } };
  const r = compare(baseline, report);
  assert.equal(r.failures.length, 2);
  assert.match(r.failures[0], /src\/A.cs: line coverage fell from 80% to 79%/);
  assert.match(r.failures[1], /src\/A.cs: branch coverage fell from 50% to 25%/);
});

test("equal or higher coverage passes, and a null baseline is not checked", () => {
  const baseline = { files: { "src/A.cs": { line: 66.66, branch: null } } };
  const report = { files: { "src/A.cs": file(2, 3, 1, 2) } };
  assert.deepEqual(compare(baseline, report).failures, []);
});

test("a file that lost all its branches or lines fails rather than passing as n/a", () => {
  const r = compare({ files: { "src/A.cs": { line: 50, branch: 50 } } }, { files: { "src/A.cs": file(0, 0, 0, 0) } });
  assert.equal(r.failures.length, 2);
});

test("a baseline file missing from the report is a note, not a failure, unless excluded", () => {
  const r = compare({ files: { "src/Gone.cs": { line: 90, branch: 90 } } }, { files: {}, excluded: [] });
  assert.deepEqual(r.failures, []);
  assert.equal(r.notes.length, 1);
  const excluded = compare({ files: { "src/Gone.cs": { line: 90, branch: 90 } } }, { files: {}, excluded: [{ file: "src/Gone.cs", reason: "x" }] });
  assert.equal(excluded.notes.length, 0);
});

test("update raises and never lowers", () => {
  const baseline = { version: 1, note: "keep", files: { "src/A.cs": { line: 90, branch: 40 } } };
  const report = { files: { "src/A.cs": file(80, 100, 3, 4), "src/N.cs": file(1, 4, 0, 0) } };
  const next = update(baseline, report);
  assert.deepEqual(next.files["src/A.cs"], { line: 90, branch: 75 });
  assert.deepEqual(next.files["src/N.cs"], { line: 25, branch: null });
  assert.equal(next.note, "keep");
});

test("update keeps entries for missing files unless pruning", () => {
  const baseline = { files: { "src/Gone.cs": { line: 90, branch: 90 } } };
  assert.ok("src/Gone.cs" in update(baseline, { files: {} }).files);
  assert.ok(!("src/Gone.cs" in update(baseline, { files: {} }, { prune: true }).files));
});
