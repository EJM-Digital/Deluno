#!/usr/bin/env node
// The coverage ratchet (issue 1268, docs/testing/STRATEGY.md): no production file may measure below its baseline.
//
//   node scripts/check-coverage-ratchet.mjs                      validate docs/testing/coverage-exclusions.json only
//   node scripts/check-coverage-ratchet.mjs --report <json>      also hold every file in the merged report to its baseline
//   node scripts/check-coverage-ratchet.mjs --report <json> --update
//                                                                raise the baseline to the report; never lowers a number
//   ... --update --prune                                         also drop baseline entries for files that no longer exist
//   --baseline <file> (default docs/testing/coverage-baseline.json)  --exclusions <file> (default docs/testing/coverage-exclusions.json)
//
// The baseline holds, per file, line and branch percent rounded DOWN to two places, so a file never fails
// against its own measurement. null means the file has nothing to measure (no lines or no branches).
// An empty baseline (the checked-in placeholder) holds nothing back: CI's first report fills it with --update,
// which the owner reviews and commits (docs/testing/COVERAGE.md, "How the baseline is filled").
// An exclusion without a reason fails the check, whether or not a report is given.
import { readFileSync, writeFileSync, existsSync } from "node:fs";
import { pathToFileURL } from "node:url";

export const floor2 = (x) => (x === null ? null : Math.floor(x * 100 + 1e-9) / 100);

const percent = (part) => (part && part.total > 0 ? (part.covered / part.total) * 100 : null);

export function validateExclusions(doc) {
  const problems = [];
  const list = doc?.exclusions;
  if (!Array.isArray(list)) return ["coverage-exclusions.json must hold an `exclusions` array."];
  list.forEach((e, i) => {
    const label = typeof e?.path === "string" ? e.path : `entry ${i}`;
    if (typeof e?.path !== "string" || e.path.trim() === "") problems.push(`Exclusion ${i} has no path.`);
    if (typeof e?.reason !== "string" || e.reason.trim().length < 10) {
      problems.push(`Excluded ${label} has no reason (write one plain sentence of at least 10 characters).`);
    }
  });
  return problems;
}

export function validateReportExclusions(report) {
  return (report.excluded ?? [])
    .filter((e) => typeof e.reason !== "string" || e.reason.trim() === "")
    .map((e) => `Excluded file ${e.file} has no reason.`);
}

// Returns { failures, notes }.
export function compare(baseline, report) {
  const failures = [];
  const notes = [];
  const entries = Object.entries(baseline.files ?? {});
  if (entries.length === 0) notes.push("The baseline is empty: nothing is held back yet. Fill it with --update.");
  for (const [file, base] of entries) {
    const now = report.files?.[file];
    if (!now) {
      if (!(report.excluded ?? []).some((e) => e.file === file)) notes.push(`${file} is in the baseline but not in the report (deleted or renamed?). Run --update --prune to drop it.`);
      continue;
    }
    for (const kind of ["line", "branch"]) {
      const was = base[kind];
      if (was === null || was === undefined) continue;
      const is = floor2(percent(kind === "line" ? now.lines : now.branches));
      if (is === null) {
        failures.push(`${file}: ${kind} coverage was ${was}% and the file now has no ${kind}s to measure.`);
      } else if (is < was) {
        failures.push(`${file}: ${kind} coverage fell from ${was}% to ${is}%.`);
      }
    }
  }
  for (const file of Object.keys(report.files ?? {})) {
    if (entries.length > 0 && !baseline.files[file]) notes.push(`${file} is new and not in the baseline yet.`);
  }
  return { failures, notes };
}

// Raises, never lowers. Returns a new baseline document.
export function update(baseline, report, { prune = false } = {}) {
  const files = { ...(baseline.files ?? {}) };
  for (const [file, now] of Object.entries(report.files ?? {})) {
    const old = files[file] ?? { line: null, branch: null };
    const line = floor2(percent(now.lines));
    const branch = floor2(percent(now.branches));
    files[file] = {
      line: line === null ? old.line ?? null : Math.max(line, old.line ?? 0),
      branch: branch === null ? old.branch ?? null : Math.max(branch, old.branch ?? 0)
    };
  }
  if (prune) {
    for (const file of Object.keys(files)) if (!(file in (report.files ?? {}))) delete files[file];
  }
  const sorted = Object.fromEntries(Object.keys(files).sort().map((k) => [k, files[k]]));
  return { ...baseline, version: 1, files: sorted };
}

function parseArgs(argv) {
  const o = { baseline: "docs/testing/coverage-baseline.json", exclusions: "docs/testing/coverage-exclusions.json" };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === "--report") o.report = argv[++i];
    else if (a === "--baseline") o.baseline = argv[++i];
    else if (a === "--exclusions") o.exclusions = argv[++i];
    else if (a === "--update") o.update = true;
    else if (a === "--prune") o.prune = true;
    else throw new Error(`Unknown argument ${a}`);
  }
  return o;
}

function main() {
  const o = parseArgs(process.argv.slice(2));
  const problems = [];
  if (!existsSync(o.exclusions)) problems.push(`${o.exclusions} is missing.`);
  else problems.push(...validateExclusions(JSON.parse(readFileSync(o.exclusions, "utf8"))));
  let notes = [];
  if (o.report) {
    const report = JSON.parse(readFileSync(o.report, "utf8"));
    const baseline = JSON.parse(readFileSync(o.baseline, "utf8"));
    problems.push(...validateReportExclusions(report));
    if (o.update) {
      if (problems.length === 0) {
        const next = update(baseline, report, { prune: o.prune });
        writeFileSync(o.baseline, JSON.stringify(next, null, 2) + "\n");
        console.log(`Baseline raised: ${Object.keys(next.files).length} files in ${o.baseline}.`);
      }
    } else {
      const result = compare(baseline, report);
      problems.push(...result.failures);
      notes = result.notes;
    }
  }
  for (const n of notes) console.log(`note: ${n}`);
  if (problems.length > 0) {
    for (const p of problems) console.error(`FAIL: ${p}`);
    process.exit(1);
  }
  console.log(o.report ? "Coverage ratchet holds." : "Coverage exclusions are valid.");
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    main();
  } catch (e) {
    console.error(e.message);
    process.exit(1);
  }
}
