import assert from "node:assert/strict";
import { test } from "node:test";
import { compareVersions, delunoImageForVersion, readRepositories } from "./deluno-image.mjs";

const repos = { deluno: "EJM-Digital/Deluno", formerDelunoImage: "ghcr.io/former-owner/deluno", lastFormerDelunoImageVersion: "1.0.0-rc.30.34" };

test("releases up to the last former one are at the former image", () => {
  for (const v of ["1.0.0-rc.3", "1.0.0-rc.30.21", "v1.0.0-rc.30.34", "0.9.0"]) assert.equal(delunoImageForVersion(v, repos), repos.formerDelunoImage, v);
});

test("later releases are at the current repository's lower-cased image", () => {
  for (const v of ["1.0.0-rc.30.35", "1.0.0-rc.31", "1.0.0-rc.30.100", "1.0.0", "1.1.0-rc.1"]) assert.equal(delunoImageForVersion(v, repos), "ghcr.io/ejm-digital/deluno", v);
});

test("a release is newer than its own candidates and versions order numerically", () => {
  assert.equal(compareVersions("1.0.0", "1.0.0-rc.30.34"), 1);
  assert.equal(compareVersions("1.0.0-rc.9", "1.0.0-rc.10"), -1);
  assert.equal(compareVersions("1.0.0-rc.30.34", "1.0.0-rc.30.34"), 0);
});

test("the repository file carries the values the helper needs", () => {
  const real = readRepositories();
  assert.match(real.deluno, /^[^/]+\/Deluno$/);
  assert.match(real.weir, /^EJM-Digital\/Weir$/);
  assert.equal(real.lastFormerDelunoImageVersion, "1.0.0-rc.30.34");
});
