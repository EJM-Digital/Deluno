#!/usr/bin/env node
// Mutation testing on the changed files only: the push job of ci.yml (issue 1269, docs/testing/STRATEGY.md measure 3).
//
//   node scripts/mutation/run-changed.mjs --base <sha> --out <dir>          run Stryker where the change reaches
//   node scripts/mutation/run-changed.mjs --base <sha> --plan               print what would run and stop
//
// .NET: Stryker.NET's diff mode (`--since:<sha>`) mutates only the lines the change touched, in every production project
// whose source changed. The Windows-only tray is left to the nightly run (it cannot build on Linux).
// JavaScript: StrykerJS has no diff mode, but `--mutate` takes line ranges (`file:start-end`), so it is given the lines the
// change added or altered in each changed source file of apps/web, sdk/typescript and services/metadata-gateway, the way
// Stryker.NET's diff mode works. A one-line fix in a large page no longer has to answer for every old line of it (29 Oct 2026:
// a #1395 fix met 887 survivors that were there before it).
// Each run leaves <dir>/<project>.json; `check-mutation-ratchet.mjs --reports <dir> --survivors` then fails on any
// survivor that docs/testing/mutation-equivalents.json does not list.
import { execFileSync } from "node:child_process";
import { mkdirSync, copyFileSync, existsSync } from "node:fs";
import path from "node:path";
import { pathToFileURL } from "node:url";

const DOTNET = /^src\/(Deluno\.[A-Za-z]+)\/(?!(?:bin|obj|Migrations)\/).*\.cs$/;
const JS_PROJECTS = [
  { name: "web", dir: "apps/web", source: /^apps\/web\/src\/.*\.(ts|tsx)$/, skip: /\.test\.tsx?$|\.d\.ts$|^apps\/web\/src\/test\// },
  { name: "sdk", dir: "sdk/typescript", source: /^sdk\/typescript\/src\/.*\.ts$/, skip: /\.d\.ts$/ },
  { name: "metadata-gateway", dir: "services/metadata-gateway", source: /^services\/metadata-gateway\/src\/.*\.js$/, skip: /$^/ }
];

// changed: repository-relative paths from `git diff --name-only`.
export function plan(changed) {
  const files = changed.map((f) => f.replace(/\\/g, "/"));
  const dotnet = [...new Set(files.map((f) => f.match(DOTNET)?.[1]).filter(Boolean))].sort();
  const js = [];
  for (const p of JS_PROJECTS) {
    const hit = files.filter((f) => p.source.test(f) && !p.skip.test(f));
    if (hit.length) js.push({ name: p.name, dir: p.dir, files: hit.map((f) => f.slice(p.dir.length + 1)).sort() });
  }
  return { dotnet, js };
}

// The lines a unified diff with no context (`git diff -U0`) adds or changes in the new file, as [start, end] pairs. A hunk that
// only removes lines leaves nothing to mutate.
export function changedRanges(diffText) {
  const ranges = [];
  for (const match of diffText.matchAll(/^@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@/gm)) {
    const start = Number(match[1]);
    const count = match[2] === undefined ? 1 : Number(match[2]);
    if (count > 0) ranges.push([start, start + count - 1]);
  }
  return ranges;
}

// StrykerJS mutate entries for one file: one `file:start-end` per changed range, or nothing when no line was added.
export function mutateEntries(file, ranges) {
  return ranges.map(([start, end]) => `${file}:${start}-${end}`);
}

function run(cmd, args, opts = {}) {
  console.log(`> ${cmd} ${args.join(" ")}`);
  execFileSync(cmd, args, { stdio: "inherit", shell: process.platform === "win32", ...opts });
}

function main() {
  const argv = process.argv.slice(2);
  const get = (k) => (argv.includes(k) ? argv[argv.indexOf(k) + 1] : undefined);
  const base = get("--base");
  const out = get("--out");
  if (!base) throw new Error("--base <sha> is required.");
  const changed = execFileSync("git", ["diff", "--name-only", `${base}...HEAD`], { encoding: "utf8" }).split("\n").map((s) => s.trim()).filter(Boolean);
  const p = plan(changed);
  console.log(`Mutation on the changed files since ${base}`);
  console.log(`  .NET projects: ${p.dotnet.join(", ") || "none"}`);
  for (const j of p.js) console.log(`  ${j.name}: ${j.files.length} file${j.files.length === 1 ? "" : "s"}`);
  if (argv.includes("--plan")) return;
  if (!out) throw new Error("--out <dir> is required.");
  mkdirSync(out, { recursive: true });

  for (const name of p.dotnet) {
    const outputDir = path.resolve("StrykerOutput", name);
    run("dotnet", ["tool", "run", "dotnet-stryker", "-f", `src/${name}/stryker-config.json`, `--since:${base}`, "--output", outputDir]);
    const report = path.join(outputDir, "reports", "mutation-report.json");
    // Stryker writes no report when the diff holds no mutable line; an empty run is a pass.
    if (existsSync(report)) copyFileSync(report, path.join(out, `${name}.json`));
    else console.log(`${name}: no mutants in the changed lines.`);
  }
  for (const j of p.js) {
    const entries = j.files.flatMap((file) => mutateEntries(file, changedRanges(
      execFileSync("git", ["diff", "-U0", `${base}...HEAD`, "--", `${j.dir}/${file}`], { encoding: "utf8" }))));
    if (entries.length === 0) {
      console.log(`${j.name}: the change only removed lines; nothing to mutate.`);
      continue;
    }
    run("npx", ["stryker", "run", "--mutate", entries.join(",")], { cwd: j.dir });
    const report = path.join(j.dir, "reports", "mutation", `${j.name}.json`);
    if (existsSync(report)) copyFileSync(report, path.join(out, `${j.name}.json`));
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  try {
    main();
  } catch (e) {
    console.error(e.message);
    process.exit(1);
  }
}
