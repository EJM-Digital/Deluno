// Run: node --test scripts/mutation/test-filter.test.mjs
import test from "node:test";
import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { filterFor, testClasses, typeNames } from "./test-filter.mjs";

function repo(files) {
  const root = mkdtempSync(path.join(tmpdir(), "mutation-filter-"));
  for (const [rel, text] of Object.entries(files)) {
    mkdirSync(path.dirname(path.join(root, rel)), { recursive: true });
    writeFileSync(path.join(root, rel), text);
  }
  return root;
}

test("a project's run is limited to the test classes that name one of its types", () => {
  const root = repo({
    "src/Deluno.Realtime/RealtimeGroups.cs": "public static class RealtimeGroups { }\npublic sealed record RealtimeEnvelope(int A);\n",
    "tests/X.Tests/GroupTests.cs": "public class GroupTests { [Fact] public void A() { _ = RealtimeGroups.All; } }\n",
    "tests/X.Tests/OtherTests.cs": "public class OtherTests { [Fact] public void A() { } }\n",
    "tests/X.Tests/Two.cs": "public class Helper { }\npublic class EnvelopeTests { [Theory] public void B() { new RealtimeEnvelope(1); } }\n"
  });
  try {
    const names = typeNames(root, "src/Deluno.Realtime");
    assert.deepEqual([...names].sort(), ["RealtimeEnvelope", "RealtimeGroups"]);
    const classes = testClasses(root);
    assert.deepEqual(classes.map((c) => c.name).sort(), ["EnvelopeTests", "GroupTests", "OtherTests"]);
    assert.equal(filterFor(names, classes), "FullyQualifiedName~EnvelopeTests|FullyQualifiedName~GroupTests");
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("a project that most of the suite names keeps the whole suite, and one that nothing names has no filter", () => {
  const classes = Array.from({ length: 5 }, (_, i) => ({ name: `T${i}`, text: "Contracts" }));
  assert.equal(filterFor(new Set(["Contracts"]), classes, 3), null);
  assert.equal(filterFor(new Set(["Nothing"]), classes), null);
  assert.equal(filterFor(new Set(), classes), null);
});
