import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { matrixEntries } from "./gen-stryker-configs.mjs";

const files = ["src/Deluno.Filesystem/stryker-config.json", "src/Deluno.Integrations/stryker-config.json", "src/Deluno.Movies/stryker-config.json"];
const risky = { projects: { "Deluno.Filesystem": "x" }, files: [{ project: "Deluno.Integrations", path: "DownloadClients/", why: "y" }, { project: "Deluno.Integrations", path: "Processors/", why: "y" }] };

test("a risky-only matrix keeps whole risky projects, narrows part-risky ones to their folders and drops the rest", () => {
  const entries = matrixEntries(files, risky);
  assert.deepEqual(entries.map((e) => e.project), ["Deluno.Filesystem", "Deluno.Integrations"]);
  assert.equal(entries[0].mutate, undefined);
  assert.equal(entries[1].mutate, "**/DownloadClients/**/*.cs,**/Processors/**/*.cs,!**/Migrations/**");
});

test("the full matrix lists every project with no mutate list", () => {
  const entries = matrixEntries(files, null);
  assert.equal(entries.length, 3);
  assert.ok(entries.every((e) => e.mutate === undefined));
});

test("every project in the checked-in risky list has a config to run", () => {
  const doc = JSON.parse(readFileSync("docs/testing/mutation-risky-areas.json", "utf8"));
  const names = matrixEntries(Object.keys({}), doc);
  assert.deepEqual(names, []);
});
