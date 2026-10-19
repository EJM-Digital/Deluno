import test from "node:test";
import assert from "node:assert/strict";
import { testFilesNotInWorkflow, listTestFiles } from "./check-node-tests-run.mjs";
import { readFileSync } from "node:fs";

test("a test file no job names is reported, and a named one is not", () => {
  const yml = "run: node --test scripts/a.test.mjs";
  assert.deepEqual(testFilesNotInWorkflow(["scripts/a.test.mjs", "scripts/b.test.mjs"], yml), ["scripts/b.test.mjs"]);
});

test("every test file in this repository is named in ci.yml", () => {
  const root = new URL("..", import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, "$1");
  const yml = readFileSync(new URL("../.github/workflows/ci.yml", import.meta.url), "utf8");
  assert.deepEqual(testFilesNotInWorkflow(listTestFiles(root), yml), []);
});
