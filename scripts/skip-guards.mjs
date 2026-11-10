// Tests that skip themselves (issue 1536). A test that quietly returns, or is marked skipped, where its condition does not hold
// passes without proving anything; Weir hit this when a test needed Windows PowerShell that its Linux CI never had.
//
// This finds every such guard in the repository's tests and holds it to docs/testing/skip-guards.json, which says for each file
// which CI job meets the condition and runs the test. The check fails when:
//   * a guard has no entry (a new one, or one more in a file whose count was recorded);
//   * an entry names a guard that is gone;
//   * an entry names a job that ci.yml does not have;
//   * a guard needs an operating system that none of the entry's jobs runs on;
//   * an entry names an environment variable the workflow never sets;
//   * a file the Windows backend job must run is not in the filter it builds (node scripts/skip-guards.mjs filter).
// A guard that is meant to be run by hand (a benchmark, a probe of live sites) says so with "manual" and the words for why.
//
// Usage: node scripts/skip-guards.mjs [check]     (the CI step)
//        node scripts/skip-guards.mjs draft       (entries for guards that have none, to complete by hand)
//        node scripts/skip-guards.mjs filter      (the dotnet test filter the Windows backend job runs)
import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

export const REGISTER = "docs/testing/skip-guards.json";
export const WINDOWS_JOB = "backend-windows";

const isTest = {
  dotnet: (f) => /^tests\/.+\.cs$/.test(f) && !/\/(obj|bin)\//.test(f),
  vitest: (f) => /^(apps\/web\/src|sdk\/typescript|services\/[^/]+)\/.*\.test\.(ts|tsx|js|mjs)$/.test(f),
  playwright: (f) => /^apps\/web\/tests\/.*\.spec\.ts$/.test(f) || /^tests\/e2e\/.*\.spec\.ts$/.test(f),
  // This check's own test is left out: its fixtures are made-up files that hold the very text the scanner looks for.
  node: (f) => /\.test\.mjs$/.test(f) && !f.startsWith("scripts/mutation/") && !f.startsWith("apps/web/") && f !== "scripts/skip-guards.test.mjs",
  powershell: (f) => /^(scripts|tests)\/.*\/?(Test|Run)-[\w.-]+\.ps1$/.test(f)
};

/** The text of a line without a trailing // comment (good enough for these checks; strings holding // are rare in a guard). */
const code = (line) => line.replace(/\/\/.*$/, "");

/** The text of the statement or block an `if` at lines[i] controls. */
function blockOf(lines, i) {
  const first = code(lines[i]);
  const afterCondition = first.slice(first.indexOf("if"));
  if (/\)\s*return\s*;/.test(afterCondition) || /\)\s*\{[^}]*\breturn\s*;[^}]*\}/.test(afterCondition)) return afterCondition;
  let text = "";
  let depth = 0;
  let opened = false;
  for (let j = i + 1; j < Math.min(lines.length, i + 40); j++) {
    const line = code(lines[j]);
    text += line + "\n";
    for (const ch of line) {
      if (ch === "{") { depth++; opened = true; }
      else if (ch === "}") depth--;
    }
    if (!opened && line.trim() !== "") return text; // a single statement
    if (opened && depth <= 0) return text;
  }
  return text;
}

const OS = { Windows: "windows", Linux: "linux" };

/** The operating systems a guard needs, from its condition: `!IsWindows()` returns off Windows, so the test needs Windows. */
function needsOf(condition) {
  if (condition.includes("&&")) return []; // returns only when all hold: some other leg runs it, no single system is needed
  const needs = new Set();
  for (const m of condition.matchAll(/(!?)\s*(?:OperatingSystem\.Is(Windows|Linux|MacOS)\(\)|RuntimeInformation\.IsOSPlatform\(OSPlatform\.(Windows|Linux|OSX)\))/g)) {
    const system = OS[m[2] ?? m[3]];
    if (!system) continue;
    if (m[1] === "!") needs.add(system);
    else needs.add(system === "windows" ? "linux" : "windows");
  }
  return [...needs].sort();
}

/** True when `if (v is null)` tests a value that a helper of this file got by asking the machine (a signed file, a tool). */
function askedTheMachine(lines, i, condition) {
  const subject = /\b(\w+)\s+is null\b|\b(\w+)\s*==\s*null\b/.exec(condition);
  if (!subject) return false;
  const name = subject[1] ?? subject[2];
  const before = lines.slice(Math.max(0, i - 3), i).join("\n");
  const call = new RegExp(String.raw`\b${name}\s*=\s*(?:\w+\.)*(\w+)\(`).exec(before);
  if (!call) return false;
  const at = lines.findIndex((l) => new RegExp(String.raw`static\s+[\w<>?\[\], ]+\s+${call[1]}\s*\(`).test(l));
  return at >= 0 && /OperatingSystem\.Is|GetEnvironmentVariable/.test(lines.slice(at, at + 12).join("\n"));
}

function dotnetGuards(text) {
  const lines = text.split(/\r?\n/);
  const found = [];
  // `static bool Name() => ...OperatingSystem... / Environment...` (or a property) used as `if (Name()) return;`. A helper that is
  // only an operating-system question stands for that question, so what the guard needs can still be read from the condition.
  const helpers = new Map();
  for (const m of text.matchAll(/static\s+bool\s+(\w+)\s*(?:\(\))?\s*(?:=>|\{)([^;]*(?:;|\}))/g)) {
    if (!/OperatingSystem\.|Environment\.|RuntimeInformation\./.test(m[2])) continue;
    const only = /^\s*(?:return\s+)?(?:OperatingSystem\.Is(Windows|Linux)\(\)|RuntimeInformation\.IsOSPlatform\(OSPlatform\.(Windows|Linux)\))\s*;?\s*\}?\s*$/.exec(m[2]);
    helpers.set(m[1], only ? `OperatingSystem.Is${only[1] ?? only[2]}()` : null);
  }
  for (let i = 0; i < lines.length; i++) {
    const line = code(lines[i]);
    if (/\bSkip\s*=\s*"/.test(line) || /\bAssert\.Skip\w*\(|\bSkip\.(If|IfNot|Unless)\(/.test(line)) {
      found.push({ kind: "skip-attribute", line: i + 1, needs: [] });
      continue;
    }
    const m = /^\s*(?:else\s+)?if\s*\((.*)$/.exec(line);
    if (!m) continue;
    let condition = m[1];
    let usesHelper = false;
    for (const [name, standsFor] of helpers) {
      const use = new RegExp(`\\b${name}\\b(?:\\(\\))?`, "g");
      if (!use.test(condition)) continue;
      if (standsFor) condition = condition.replace(use, standsFor);
      else usesHelper = true;
    }
    const os = /OperatingSystem\.Is|RuntimeInformation\.IsOSPlatform/.test(condition);
    // A variable that turns something ON (a recording mode that then returns) is not a skip; one whose absence returns is.
    const env = /Environment\.GetEnvironmentVariable/.test(condition) && /\bis not\b|IsNullOrEmpty|IsNullOrWhiteSpace|==\s*null|!=|!\s*string\.Equals/.test(condition);
    const user = /Environment\.IsPrivilegedProcess|Environment\.UserName/.test(condition);
    // The result of a lookup that found nothing (a signed file, a tool) in a file that asks the machine questions.
    const lookup = askedTheMachine(lines, i, condition);
    // A variable read first and tested after, or a tool or file that must exist.
    const absent = /File\.Exists|Directory\.Exists|IsNullOrEmpty|IsNullOrWhiteSpace/.test(condition);
    if (!os && !env && !user && !usesHelper && !lookup && !absent) continue;
    const block = blockOf(lines, i);
    // A bare `return;` leaves the test; `return value;` answers a question (a lookup of a tool) and is not a skip.
    if (!/\breturn\s*;|\bAssert\.Skip|\bSkip\./.test(block)) continue;
    const kind = os || usesHelper ? "operating-system" : env ? "environment-variable" : user ? "user" : "lookup";
    found.push({ kind, line: i + 1, needs: os ? needsOf(condition) : [] });
  }
  return found;
}

/** A JavaScript `if` on the platform, an environment variable or a file or tool that then leaves the test with a bare return. */
function scriptGuards(text, kind) {
  const lines = text.split(/\r?\n/);
  const found = [];
  lines.forEach((raw, i) => {
    const line = code(raw);
    if (!/^\s*(?:\}\s*else\s+)?if\s*\(.*(process\.platform|process\.env|existsSync|which\(|commandExists|hasCommand)/.test(line)) return;
    const near = [line, code(lines[i + 1] ?? ""), code(lines[i + 2] ?? "")].join("\n");
    if (/\breturn\s*;|\breturn\s*\}|\bt\.skip\(|\bctx\.skip\(/.test(near) || /\)\s*return\s*$/.test(line)) found.push({ kind, line: i + 1, needs: [] });
  });
  return found;
}

function matchesAt(text, pattern, kind) {
  const found = [];
  text.split(/\r?\n/).forEach((line, i) => {
    if (pattern.test(code(line))) found.push({ kind, line: i + 1, needs: [] });
  });
  return found;
}

function guardsOf(file, text) {
  if (isTest.dotnet(file)) return dotnetGuards(text);
  if (isTest.playwright(file)) return matchesAt(text, /\btest(?:\.describe)?\.(skip|fixme)\s*\(|\btestInfo\.(skip|fixme)\s*\(|\btest\.slow\b.*skip/, "playwright-skip");
  if (isTest.vitest(file)) {
    return [
      ...matchesAt(text, /\b(?:it|test|describe)\.(?:skip|skipIf|todo|runIf)\b|\bdescribe\.skipIf\b|\bxit\(|\bxdescribe\(|\bctx\.skip\(/, "vitest-skip"),
      ...scriptGuards(text, "vitest-skip")
    ];
  }
  if (isTest.node(file)) {
    if (!/node:test/.test(text)) return [];
    const marked = matchesAt(text, /\{\s*skip\b|\bskip\s*:|^\s*(?:test|it|t|describe)\.skip\(|^\s*(?:await\s+)?t\.skip\(/, "node-test-skip");
    const early = scriptGuards(text, "node-test-skip").filter((g) => !marked.some((m) => m.line === g.line));
    return [...marked, ...early];
  }
  if (isTest.powershell(file)) {
    return matchesAt(text, /-ne\s+'Windows_NT'|\$IsWindows\b|\$IsLinux\b|skipped \(no /, "powershell-skip");
  }
  return [];
}

export function listFiles(root) {
  const out = execFileSync("git", ["ls-files", "*.cs", "*.ts", "*.tsx", "*.js", "*.mjs", "*.ps1"], { cwd: root, encoding: "utf8", maxBuffer: 1 << 26 });
  return out.split(/\r?\n/).filter((f) => f && !f.includes("node_modules/"));
}

/** Every guard, grouped by file and kind: [{ file, kind, count, needs }]. */
export function scanGuards(root, files = listFiles(root), read = (f) => readFileSync(join(root, f), "utf8")) {
  const groups = new Map();
  for (const file of files) {
    if (!Object.values(isTest).some((test) => test(file))) continue;
    for (const guard of guardsOf(file, read(file))) {
      const key = `${file}\u0000${guard.kind}`;
      const group = groups.get(key) ?? { file, kind: guard.kind, count: 0, needs: new Set() };
      group.count++;
      guard.needs.forEach((n) => group.needs.add(n));
      groups.set(key, group);
    }
  }
  return [...groups.values()].map((g) => ({ ...g, needs: [...g.needs].sort() })).sort((a, b) => a.file.localeCompare(b.file) || a.kind.localeCompare(b.kind));
}

/** The jobs of a workflow with the system each runs on: { job: "windows" | "linux" }. */
export function jobsOf(workflowText) {
  const jobs = {};
  const start = workflowText.search(/^jobs:\s*$/m);
  const lines = workflowText.slice(start).split(/\r?\n/);
  let current = null;
  for (const line of lines.slice(1)) {
    const name = /^  ([\w-]+):\s*$/.exec(line);
    if (name) { current = name[1]; jobs[current] = "linux"; continue; }
    if (current && /^    runs-on:/.test(line)) jobs[current] = /windows/i.test(line) ? "windows" : "linux";
  }
  return jobs;
}

/** The names of the test classes a C# file declares (attribute classes are not tests). */
export function classesOf(text) {
  return [...text.matchAll(/\b(?:public|internal)\s+(?:(?:sealed|static|partial|abstract)\s+)*class\s+(\w+)/g)]
    .map((m) => m[1]).filter((name) => !name.endsWith("Attribute"));
}

export function filterFor(entries, read) {
  const names = new Set();
  for (const entry of entries) {
    if (!entry.runsIn?.includes(WINDOWS_JOB) || !entry.file.endsWith(".cs")) continue;
    classesOf(read(entry.file)).forEach((c) => names.add(c));
  }
  return [...names].sort().map((c) => `FullyQualifiedName~.${c}.`).join("|");
}

/** One line per problem; empty means every guard is accounted for. */
export function problems({ guards, register, workflowText, read }) {
  const out = [];
  const jobs = jobsOf(workflowText);
  const entries = register.entries ?? [];
  const keyOf = (e) => `${e.file}\u0000${e.kind}`;
  const registered = new Map(entries.map((e) => [keyOf(e), e]));
  const seen = new Set();

  for (const guard of guards) {
    const entry = registered.get(keyOf(guard));
    seen.add(keyOf(guard));
    const where = `${guard.file} (${guard.kind})`;
    if (!entry) {
      out.push(`${where}: ${guard.count} skip guard(s) with no entry in ${REGISTER}. Say which CI job runs this test (node scripts/skip-guards.mjs draft), add a job that does, or remove the guard.`);
      continue;
    }
    if (entry.count !== guard.count) out.push(`${where}: ${guard.count} guard(s) found, ${entry.count} recorded. Re-check what each needs and update the count in ${REGISTER}.`);
  }
  for (const entry of entries) {
    const where = `${entry.file} (${entry.kind})`;
    if (!seen.has(keyOf(entry))) { out.push(`${where}: recorded in ${REGISTER} but no such guard is in the file any more. Remove the entry.`); continue; }
    const guard = guards.find((g) => keyOf(g) === keyOf(entry));
    if (entry.manual) {
      if (!entry.reason || !entry.how) out.push(`${where}: a manual guard needs "reason" (why no CI job runs it) and "how" (the command that runs it).`);
      if ((entry.runsIn ?? []).length) out.push(`${where}: a manual guard names no CI job; "runsIn" must be empty.`);
      continue;
    }
    if (!entry.runsIn?.length) { out.push(`${where}: names no CI job in "runsIn" (and is not marked manual).`); continue; }
    for (const job of entry.runsIn) {
      if (!(job in jobs)) out.push(`${where}: runs in "${job}", which ci.yml has no job of that name.`);
    }
    const systems = new Set(entry.runsIn.map((job) => jobs[job]).filter(Boolean));
    for (const need of guard.needs) {
      if (!systems.has(need)) out.push(`${where}: the test needs ${need}, and none of ${entry.runsIn.join(", ")} runs on ${need}.`);
    }
    for (const need of entry.needs ?? []) {
      if (!systems.has(need)) out.push(`${where}: the test needs ${need}, and none of ${entry.runsIn.join(", ")} runs on ${need}.`);
    }
    for (const name of entry.env ?? []) {
      if (!workflowText.includes(name)) out.push(`${where}: needs the variable ${name}, which ci.yml never sets.`);
    }
    if (entry.runsIn.includes(WINDOWS_JOB) && !filterFor([entry], read)) out.push(`${where}: runs in ${WINDOWS_JOB} but declares no test class the filter can name.`);
  }
  return out;
}

function main() {
  const root = join(dirname(fileURLToPath(import.meta.url)), "..");
  const read = (f) => readFileSync(join(root, f), "utf8");
  const mode = process.argv[2] ?? "check";
  const register = JSON.parse(read(REGISTER));
  if (mode === "filter") { process.stdout.write(filterFor(register.entries, read)); return; }
  const guards = scanGuards(root);
  if (mode === "draft") {
    const known = new Set(register.entries.map((e) => `${e.file}\u0000${e.kind}`));
    console.log(JSON.stringify(guards.filter((g) => !known.has(`${g.file}\u0000${g.kind}`)), null, 2));
    return;
  }
  const found = problems({ guards, register, workflowText: read(".github/workflows/ci.yml"), read });
  if (found.length) {
    console.error("Skip guards without a CI job that runs the test (issue 1536):\n  " + found.join("\n  "));
    process.exit(1);
  }
  console.log(`Every skip guard (${guards.reduce((n, g) => n + g.count, 0)} in ${guards.length} places) has a CI job that runs its test, or says why it is run by hand.`);
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) main();
