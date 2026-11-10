import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { classesOf, filterFor, jobsOf, problems, scanGuards } from "./skip-guards.mjs";

const workflow = `
jobs:
  backend:
    runs-on: \${{ vars.DELUNO_LINUX_RUNS_ON && fromJSON(vars.DELUNO_LINUX_RUNS_ON) || 'ubuntu-latest' }}
  backend-windows:
    runs-on: \${{ vars.DELUNO_WINDOWS_RUNS_ON && fromJSON(vars.DELUNO_WINDOWS_RUNS_ON) || 'windows-latest' }}
    env:
      DELUNO_FFMPEG_DIR: x
`;

/** Scans one made-up file at the given path. */
const scan = (file, text) => scanGuards("/nowhere", [file], () => text);

const WINDOWS_TEST = `public sealed class WinTests
{
    [Fact]
    public void A()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        Assert.True(true);
    }
}`;

test("a test that returns off Windows is a guard that needs Windows", () => {
  const [guard] = scan("tests/X.Tests/WinTests.cs", WINDOWS_TEST);
  assert.deepEqual({ kind: guard.kind, count: guard.count, needs: guard.needs }, { kind: "operating-system", count: 1, needs: ["windows"] });
});

test("a test that returns on Windows needs Linux, and a one-line return counts", () => {
  const [guard] = scan("tests/X.Tests/NixTests.cs", `class T {
 void A() {
  if (OperatingSystem.IsWindows()) return;
 }
}`);
  assert.deepEqual(guard.needs, ["linux"]);
});

test("a branch on the system that returns nothing is not a guard", () => {
  assert.deepEqual(scan("tests/X.Tests/T.cs", `class T {
 void A() {
  if (OperatingSystem.IsWindows()) { Do(); } else { Other(); }
 }
}`), []);
  assert.deepEqual(scan("tests/X.Tests/T.cs", `class T {
 string F() {
  if (OperatingSystem.IsWindows()) { return "a"; }
  return null;
 }
}`), []);
});

test("a helper or property that asks the system is followed", () => {
  const [guard] = scan("tests/X.Tests/T.cs", `class T {
 static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
 void A() {
  if (!IsWindows) return;
 }
}`);
  assert.deepEqual(guard.needs, ["windows"]);
});

test("a recording switch that returns when it is ON is not a skip, but a variable whose absence returns is", () => {
  assert.deepEqual(scan("tests/X.Tests/T.cs", `class T {
 void A() {
  if (Environment.GetEnvironmentVariable("RECORD") == "1") { return; }
 }
}`), []);
  const [guard] = scan("tests/X.Tests/T.cs", `class T {
 void A() {
  if (Environment.GetEnvironmentVariable("LIVE") is not { Length: > 0 } x) { return; }
 }
}`);
  assert.equal(guard.kind, "environment-variable");
});

test("a Skip reason on an attribute, a Playwright skip, a vitest skip, a node:test skip and a PowerShell skip are all found", () => {
  assert.equal(scan("tests/X.Tests/T.cs", 'class A : FactAttribute { A() { Skip = "no tool"; } }')[0].kind, "skip-attribute");
  assert.equal(scan("apps/web/tests/smoke/a.spec.ts", 'test.skip(isMobile, "why");')[0].kind, "playwright-skip");
  assert.equal(scan("apps/web/src/a.test.ts", 'it.skip("x", () => {});')[0].kind, "vitest-skip");
  assert.equal(scan("scripts/a.test.mjs", 'import test from "node:test";\ntest("x", { skip }, () => {});')[0].kind, "node-test-skip");
  assert.equal(scan("scripts/golden-path/Test-X.ps1", "if ($env:OS -ne 'Windows_NT') { exit 0 }")[0].kind, "powershell-skip");
});

test("files that are not tests are not scanned", () => {
  assert.deepEqual(scan("src/Deluno.Api/X.cs", WINDOWS_TEST), []);
});

test("jobs are read with the system each runs on", () => {
  assert.deepEqual(jobsOf(workflow), { backend: "linux", "backend-windows": "windows" });
});

const guard = { file: "tests/X.Tests/WinTests.cs", kind: "operating-system", count: 1, needs: ["windows"] };
const entry = { ...guard, runsIn: ["backend-windows"], reason: "r" };
const run = (over = {}) => problems({ guards: over.guards ?? [guard], register: { entries: over.entries ?? [entry] }, workflowText: workflow, read: () => WINDOWS_TEST });

test("a registered guard whose job runs on the right system passes", () => {
  assert.deepEqual(run(), []);
});

test("a guard with no entry fails and says what to do", () => {
  assert.match(run({ entries: [] }).join("\n"), /no entry in docs\/testing\/skip-guards\.json/);
});

test("another guard in a registered file fails until the count is updated", () => {
  assert.match(run({ guards: [{ ...guard, count: 2 }] }).join("\n"), /2 guard\(s\) found, 1 recorded/);
});

test("an entry for a guard that is gone fails", () => {
  assert.match(run({ guards: [] }).join("\n"), /no such guard is in the file any more/);
});

test("a job that ci.yml does not have fails", () => {
  assert.match(run({ entries: [{ ...entry, runsIn: ["nowhere"] }] }).join("\n"), /no job of that name/);
});

test("a Windows-only test whose only job is Linux fails (the Weir mistake)", () => {
  assert.match(run({ entries: [{ ...entry, runsIn: ["backend"] }] }).join("\n"), /needs windows, and none of backend runs on windows/);
});

test("an entry that names a variable the workflow never sets fails", () => {
  assert.match(run({ entries: [{ ...entry, env: ["NEVER_SET"] }] }).join("\n"), /NEVER_SET/);
  assert.deepEqual(run({ entries: [{ ...entry, env: ["DELUNO_FFMPEG_DIR"] }] }), []);
});

test("a manual guard needs a reason and a command, and names no job", () => {
  const manual = { ...guard, count: 1, manual: true, runsIn: [] };
  assert.match(run({ entries: [manual] }).join("\n"), /needs "reason".*"how"/);
  assert.deepEqual(run({ entries: [{ ...manual, reason: "r", how: "run it" }] }), []);
  assert.match(run({ entries: [{ ...manual, reason: "r", how: "h", runsIn: ["backend"] }] }).join("\n"), /runsIn" must be empty/);
});

test("an entry with no job and not manual fails", () => {
  assert.match(run({ entries: [{ ...entry, runsIn: [] }] }).join("\n"), /names no CI job/);
});

test("the Windows job's filter names each test class of the files registered to it, and not attribute classes", () => {
  assert.deepEqual(classesOf("public sealed class A {}\npublic sealed class RequiresFactAttribute : FactAttribute {}\ninternal static class B {}"), ["A", "B"]);
  assert.equal(filterFor([entry], () => WINDOWS_TEST), "FullyQualifiedName~.WinTests.");
  assert.equal(filterFor([{ ...entry, runsIn: ["backend"] }], () => WINDOWS_TEST), "");
});

// ---- the gaps the review of the first version named

test("a platform early-return in a node test is a guard (the shape Weir hit)", () => {
  const text = `import test from "node:test";
test("needs windows", () => {
  if (process.platform !== "win32") return;
  run();
});`;
  const [guard] = scan("scripts/a.test.mjs", text);
  assert.equal(guard.kind, "node-test-skip");
  const braces = `import test from "node:test";
test("needs powershell", () => {
  if (!existsSync(tool)) {
    return;
  }
});`;
  assert.equal(scan("scripts/b.test.mjs", braces).length, 1);
  const env = `import test from "node:test";
test("needs the image", () => {
  if (!process.env.IMAGE) { return; }
});`;
  assert.equal(scan("scripts/c.test.mjs", env).length, 1);
});

test("test.skip and t.skip in node:test are guards", () => {
  assert.equal(scan("scripts/a.test.mjs", `import test from "node:test";\ntest.skip("later", () => {});`)[0].kind, "node-test-skip");
  assert.equal(scan("scripts/b.test.mjs", `import test from "node:test";\ntest("x", (t) => {\n  t.skip("no tool");\n});`)[0].kind, "node-test-skip");
});

test("a message that merely mentions test.skip is not a guard", () => {
  assert.deepEqual(scan("scripts/a.test.mjs", "import test from \"node:test\";\nassert.ok(x, `a skip with no reason: test.skip(${call})`);"), []);
});

test("a variable read first and checked after, and a missing file, are guards", () => {
  const variable = `class T {
 void A() {
  var dir = Environment.GetEnvironmentVariable("TOOLS");
  if (string.IsNullOrEmpty(dir))
  {
   return;
  }
 }
}`;
  assert.equal(scan("tests/X.Tests/A.cs", variable)[0].kind, "lookup");
  const file = `class T {
 void A() {
  if (!File.Exists(tool)) return;
 }
}`;
  assert.equal(scan("tests/X.Tests/B.cs", file)[0].kind, "lookup");
});

test("a vitest platform early-return and testInfo.skip are guards", () => {
  assert.equal(scan("apps/web/src/a.test.ts", `it("x", () => {\n  if (process.platform === "win32") return;\n});`)[0].kind, "vitest-skip");
  assert.equal(scan("apps/web/tests/smoke/a.spec.ts", `testInfo.skip(true, "why");`)[0].kind, "playwright-skip");
});

// ---- the real repository

test("the real repository passes its own check: every guard has an entry and a job (the review found the first version failing on itself)", () => {
  const root = fileURLToPath(new URL("..", import.meta.url));
  const result = spawnSync(process.execPath, [join(root, "scripts", "skip-guards.mjs")], { cwd: root, encoding: "utf8" });
  assert.equal(result.status, 0, result.stderr);
  assert.match(result.stdout, /Every skip guard/);
});

test("the checker's own test file is not scanned, so its fixtures are not guards", () => {
  assert.deepEqual(scan("scripts/skip-guards.test.mjs", `import test from "node:test";\ntest("x", { skip }, () => {});`), []);
});
