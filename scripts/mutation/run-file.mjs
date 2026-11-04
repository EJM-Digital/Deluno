#!/usr/bin/env node
// Mutation testing of one web source file against its own tests only (issue 1329, docs/testing/STRATEGY.md measure 3).
//
//   node scripts/mutation/run-file.mjs <src/path/file.tsx> [--tests a.test.tsx,b.test.ts] [--lines 10-40] [--concurrency 3]
//
// `npm run mutation` and the CI changed-files run start by running the whole web suite, about two minutes before the first
// mutant. This limits vitest to the tests that import the file (found by its name, or named with --tests), so one file takes
// about a minute plus its mutants. It writes its configs to the temp folder, never into the repository, and prints each
// mutant that survived as `file:line:column Status Mutator -> replacement | source line`.
import { execFileSync } from "node:child_process";
import { existsSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";

const argv = process.argv.slice(2);
const get = (key) => (argv.includes(key) ? argv[argv.indexOf(key) + 1] : undefined);
const file = argv[0];
if (!file || file.startsWith("--")) {
  console.error("Usage: node scripts/mutation/run-file.mjs <src/path/file.tsx> [--tests a.test.tsx,b.test.ts] [--lines 10-40] [--concurrency 3]");
  process.exit(2);
}

const webDir = path.resolve(import.meta.dirname, "..", "..", "apps", "web");
const source = file.replace(/\\/g, "/").replace(/^apps\/web\//, "");
if (!existsSync(path.join(webDir, source))) throw new Error(`${source} is not under apps/web.`);

function testFilesImporting(name) {
  const found = [];
  const pattern = new RegExp(`["'/]${name.replace(/[.]/g, "[.]")}["']`);
  (function walk(dir) {
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      const full = path.join(dir, entry.name);
      if (entry.isDirectory()) walk(full);
      else if (/\.test\.tsx?$/.test(entry.name) && pattern.test(readFileSync(full, "utf8"))) found.push(path.relative(webDir, full).replace(/\\/g, "/"));
    }
  })(path.join(webDir, "src"));
  return found;
}

const base = path.basename(source).replace(/\.tsx?$/, "");
// Its own tests first (name.test.tsx, name.something.test.tsx beside it); every test that imports it only when it has none.
const own = readdirSync(path.join(webDir, path.dirname(source))).filter((name) => name.startsWith(base + ".") && /\.test\.tsx?$/.test(name)).map((name) => path.posix.join(path.dirname(source), name));
const tests = get("--tests")?.split(",").filter(Boolean) ?? (own.length > 0 ? own : testFilesImporting(base));
if (tests.length === 0) throw new Error(`No test imports ${base}. Name them with --tests.`);
console.log(`Mutating ${source} against ${tests.length} test file${tests.length === 1 ? "" : "s"}:\n  ${tests.join("\n  ")}`);

const work = mkdtempSync(path.join(tmpdir(), "deluno-mutation-"));
// Inside apps/web, so that Stryker copies it into its sandbox with the sources; removed again afterwards.
const vitestConfigName = `.mutation-${process.pid}.vitest.config.mts`;
const vitestConfig = path.join(webDir, vitestConfigName);
const report = path.join(work, "report.json");
writeFileSync(vitestConfig, `import base from "./vite.config";\nexport default { ...base, test: { ...base.test, include: ${JSON.stringify(tests)} } };\n`);

const stryker = JSON.parse(readFileSync(path.join(webDir, "stryker.config.json"), "utf8"));
const range = get("--lines");
Object.assign(stryker, {
  vitest: { configFile: vitestConfigName },
  mutate: [range ? `${source}:${range}` : source],
  reporters: ["json"],
  jsonReporter: { fileName: report },
  concurrency: Number(get("--concurrency") ?? 3)
});
const strykerConfig = path.join(work, "stryker.config.json");
writeFileSync(strykerConfig, JSON.stringify(stryker, null, 1));

try {
  execFileSync(process.platform === "win32" ? "npx.cmd" : "npx", ["stryker", "run", strykerConfig], { cwd: webDir, stdio: "inherit", shell: process.platform === "win32" });
} finally {
  rmSync(vitestConfig, { force: true });
}

const result = JSON.parse(readFileSync(report, "utf8"));
let killed = 0;
let total = 0;
for (const data of Object.values(result.files)) {
  const lines = data.source.split("\n");
  for (const mutant of data.mutants) {
    if (mutant.status === "Ignored" || mutant.status === "CompileError") continue;
    total += 1;
    if (mutant.status === "Killed" || mutant.status === "Timeout") { killed += 1; continue; }
    const { line, column } = mutant.location.start;
    console.log(`${path.basename(source)}:${line}:${column} ${mutant.status} ${mutant.mutatorName} -> ${(mutant.replacement ?? "").replace(/\n/g, " ").slice(0, 60)} | ${lines[line - 1]?.trim().slice(0, 100)}`);
  }
}
console.log(`\n${source}: ${killed} of ${total} mutants killed${total ? ` (${((killed / total) * 100).toFixed(1)}%)` : ""}. Report: ${report}`);

