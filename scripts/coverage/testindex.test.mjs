import { test } from "node:test";
import assert from "node:assert/strict";
import { proofTypeOf, slash, squash } from "./testindex.mjs";

// The proof type decides the level a row reaches, so every kind of file is pinned here: the CI end-to-end suite
// (tests/e2e on Docker Compose, tests/e2e-windows on GitHub's Windows runner) proves as real, the rig's scenarios
// as scenario, and the rest by what they are.
test("each kind of test file is read as its own proof type", () => {
  assert.equal(proofTypeOf("tests/e2e/films.spec.ts"), "e2e");
  assert.equal(proofTypeOf("tests/e2e/resendcases.ts"), "e2e");
  assert.equal(proofTypeOf("tests/e2e-windows/install.spec.ts"), "windows-e2e");
  assert.equal(proofTypeOf("scripts/golden-path/suite/vm/scenarios/G02-Films.ps1"), "scenario");
  assert.equal(proofTypeOf("apps/web/tests/smoke/navigation.spec.ts"), "smoke");
  assert.equal(proofTypeOf("apps/web/src/routes/calendar-page.test.tsx"), "vitest");
  assert.equal(proofTypeOf("apps/web/src/lib/format.test.ts"), "vitest");
  assert.equal(proofTypeOf("tests/Deluno.Worker.Tests/SlowValueTests.cs"), "backend");
});

test("a C# test is a recording replay or a real-file test only when the index says so", () => {
  const index = (replays, real) => ({ replaysRecording: () => replays, realFs: () => ({ real }) });
  assert.equal(proofTypeOf("tests/A.cs", index(true, false), "T"), "recording-replay");
  assert.equal(proofTypeOf("tests/A.cs", index(false, true), "T"), "real-fs");
  assert.equal(proofTypeOf("tests/A.cs", index(false, false), "T"), "backend");
});

test("paths and names are compared the same way on every OS", () => {
  assert.equal(slash("tests\\e2e\\films.spec.ts"), "tests/e2e/films.spec.ts");
  assert.equal(squash("A film, sent to qBittorrent!"), "afilmsenttoqbittorrent");
});
