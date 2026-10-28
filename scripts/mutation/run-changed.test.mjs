// The changed-file mutation run gives StrykerJS only the lines a change added or altered (scripts/mutation/run-changed.mjs).
import { test } from "node:test";
import assert from "node:assert/strict";
import { changedRanges, mutateEntries, plan } from "./run-changed.mjs";

test("the ranges are the added or changed lines of each hunk, and a hunk that only removes lines gives none", () => {
  const diff = [
    "diff --git a/apps/web/src/a.tsx b/apps/web/src/a.tsx",
    "@@ -10,2 +10,3 @@ export function A() {",
    "@@ -40 +41 @@",
    "@@ -60,4 +61,0 @@",
    "@@ -0,0 +1,25 @@"
  ].join("\n");
  assert.deepEqual(changedRanges(diff), [[10, 12], [41, 41], [1, 25]]);
  assert.deepEqual(changedRanges(""), []);
});

test("each range becomes one file:start-end entry, and no range gives no entry", () => {
  assert.deepEqual(mutateEntries("src/a.tsx", [[10, 12], [41, 41]]), ["src/a.tsx:10-12", "src/a.tsx:41-41"]);
  assert.deepEqual(mutateEntries("src/a.tsx", []), []);
});

test("the plan picks changed web sources and leaves tests and declaration files out", () => {
  const p = plan(["apps/web/src/lib/x.ts", "apps/web/src/lib/x.test.ts", "apps/web/src/types.d.ts", "src/Deluno.Api/A.cs", "docs/a.md"]);
  assert.deepEqual(p.dotnet, ["Deluno.Api"]);
  assert.deepEqual(p.js, [{ name: "web", dir: "apps/web", files: ["src/lib/x.ts"] }]);
});
