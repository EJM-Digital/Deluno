#!/usr/bin/env node
// The mutation ratchet (issue 1269, docs/testing/STRATEGY.md measure 3).
//
//   node scripts/check-mutation-ratchet.mjs
//       validate docs/testing/mutation-thresholds.json and docs/testing/mutation-equivalents.json only
//   node scripts/check-mutation-ratchet.mjs --reports <dir>
//       full run: every <dir>/<project>.json (Stryker's mutation-testing-elements JSON report) must score at or above
//       that project's checked-in threshold. Survivors are listed in the summary but do not fail by themselves.
//   node scripts/check-mutation-ratchet.mjs --reports <dir> --update
//       raise the thresholds to the scores in the reports; never lowers a number
//   node scripts/check-mutation-ratchet.mjs --reports <dir> --survivors
//       changed-files run (Stryker --since): a mutant that survived or was never covered fails when it is in a risky area
//       (docs/testing/mutation-risky-areas.json: imports, deletes, download clients, the processor hand-off, files on disk),
//       unless docs/testing/mutation-equivalents.json lists it with a reason. Survivors elsewhere are listed, not failed.
//   node scripts/check-mutation-ratchet.mjs --reports <dir> --summary
//       print the score per project and every unlisted survivor as markdown (the nightly job summary)
//   node scripts/check-mutation-ratchet.mjs --base <ref>
//       the floor ratchet, the one mutation check CI runs (it never runs mutation and gates on no score): fails when a project's
//       floor in the thresholds file is lower than at <ref>, or a project was removed, unless the file's top-level `lowered`
//       object has an entry { "<project>": "<reason>" } for it. A missing file at <ref> or an unreachable <ref> passes.
//   --thresholds <file> --equivalents <file> --risky <file> override the default paths.
//
// The score is (killed + timeout) / (killed + timeout + survived + no coverage), the same for Stryker.NET and StrykerJS.
// An equivalent mutant is one no test can tell apart from the original code (for example `i < n` to `i != n` in a
// loop that always ends exactly at n); each entry names the project, file, mutator, line and replacement, and says why.
import { readFileSync, writeFileSync, existsSync, readdirSync } from "node:fs";
import { execFileSync } from "node:child_process";
import path from "node:path";
import { pathToFileURL } from "node:url";

export const floor2 = (x) => Math.floor(x * 100 + 1e-9) / 100;

const KILLED = new Set(["Killed", "Timeout"]);
const MISSED = new Set(["Survived", "NoCoverage"]);

export function normalizeFile(file) {
  return String(file).replace(/\\/g, "/").replace(/^\.\//, "");
}

// Every mutant that counts, with the file it is in.
export function mutants(report) {
  const out = [];
  for (const [file, data] of Object.entries(report.files ?? {})) {
    for (const m of data.mutants ?? []) {
      out.push({
        file: normalizeFile(file),
        id: m.id,
        mutator: m.mutatorName,
        line: m.location?.start?.line,
        replacement: m.replacement ?? "",
        status: m.status
      });
    }
  }
  return out;
}

// Percent, or null when nothing was measured.
export function score(report) {
  let killed = 0;
  let missed = 0;
  for (const m of mutants(report)) {
    if (KILLED.has(m.status)) killed++;
    else if (MISSED.has(m.status)) missed++;
  }
  return killed + missed === 0 ? null : (killed / (killed + missed)) * 100;
}

export function survivors(report) {
  return mutants(report).filter((m) => MISSED.has(m.status));
}

export function validateThresholds(doc) {
  const problems = [];
  if (!doc || typeof doc.projects !== "object" || doc.projects === null) return ["mutation-thresholds.json must hold a `projects` object."];
  if (doc.lowered !== undefined) {
    if (typeof doc.lowered !== "object" || doc.lowered === null || Array.isArray(doc.lowered)) problems.push("`lowered` must be an object of project name to reason.");
    else {
      for (const [name, why] of Object.entries(doc.lowered)) {
        if (typeof why !== "string" || why.trim().length < 20) problems.push(`lowered.${name} needs a reason of at least 20 characters.`);
      }
    }
  }
  for (const [name, t] of Object.entries(doc.projects)) {
    if (typeof t?.score !== "number" || t.score < 0 || t.score > 100) problems.push(`Threshold for ${name} must be a number from 0 to 100.`);
  }
  return problems;
}

export function validateEquivalents(doc) {
  const list = doc?.equivalents;
  if (!Array.isArray(list)) return ["mutation-equivalents.json must hold an `equivalents` array."];
  const problems = [];
  list.forEach((e, i) => {
    const label = e?.file ? `${e.file}:${e.line}` : `entry ${i}`;
    for (const key of ["project", "file", "mutator"]) {
      if (typeof e?.[key] !== "string" || e[key].trim() === "") problems.push(`Equivalent mutant ${label} has no ${key}.`);
    }
    if (!Number.isInteger(e?.line)) problems.push(`Equivalent mutant ${label} has no line number.`);
    if (typeof e?.reason !== "string" || e.reason.trim().length < 20) {
      problems.push(`Equivalent mutant ${label} has no reason (say in a sentence of at least 20 characters why no test can tell it from the original).`);
    }
  });
  return problems;
}

const sameFile = (reportFile, listed) => reportFile === listed || reportFile.endsWith(`/${listed}`) || listed.endsWith(`/${reportFile}`);

export function isListed(equivalents, project, m) {
  return (equivalents.equivalents ?? []).some(
    (e) => e.project === project && sameFile(m.file, normalizeFile(e.file)) && e.mutator === m.mutator && e.line === m.line && (e.replacement === undefined || e.replacement === m.replacement)
  );
}

// Returns { failures, notes } for a full run.
export function compare(thresholds, scores) {
  const failures = [];
  const notes = [];
  for (const [name, now] of Object.entries(scores)) {
    const t = thresholds.projects?.[name];
    if (!t) {
      notes.push(`${name} has no threshold yet; add it with --update.`);
      continue;
    }
    if (now === null) {
      notes.push(`${name} had nothing to measure in this run.`);
      continue;
    }
    if (floor2(now) < t.score) failures.push(`${name}: mutation score fell from ${t.score}% to ${floor2(now)}%.`);
  }
  return { failures, notes };
}

// The floor ratchet: `before` and `after` are parsed thresholds files. A lowered or removed floor fails unless `after.lowered` explains it.
export function compareFloors(before, after) {
  const failures = [];
  const lowered = after?.lowered ?? {};
  const has = (name) => typeof lowered[name] === "string" && lowered[name].trim().length >= 20;
  for (const [name, t] of Object.entries(before?.projects ?? {})) {
    const now = after?.projects?.[name];
    if (!now) {
      if (!has(name)) failures.push(`${name}: its mutation floor (${t.score}%) was removed. Put a reason in \`lowered.${name}\` of mutation-thresholds.json, or keep the floor.`);
    } else if (now.score < t.score && !has(name)) {
      failures.push(`${name}: its mutation floor was lowered from ${t.score}% to ${now.score}%. Put a reason in \`lowered.${name}\` of mutation-thresholds.json, or keep the floor.`);
    }
  }
  return failures;
}

// Raises, never lowers.
export function update(thresholds, scores) {
  const projects = { ...(thresholds.projects ?? {}) };
  for (const [name, now] of Object.entries(scores)) {
    if (now === null) continue;
    projects[name] = { score: Math.max(floor2(now), projects[name]?.score ?? 0) };
  }
  const sorted = Object.fromEntries(Object.keys(projects).sort().map((k) => [k, projects[k]]));
  return { ...thresholds, version: 1, projects: sorted };
}

export function unlistedSurvivors(equivalents, project, report) {
  return survivors(report).filter((m) => !isListed(equivalents, project, m));
}

export function validateRisky(doc) {
  const problems = [];
  if (!doc || typeof doc.projects !== "object" || doc.projects === null || !Array.isArray(doc.files)) {
    return ["mutation-risky-areas.json must hold a `projects` object and a `files` array."];
  }
  for (const [name, why] of Object.entries(doc.projects)) {
    if (typeof why !== "string" || why.trim() === "") problems.push(`Risky project ${name} has no reason.`);
  }
  doc.files.forEach((f, i) => {
    for (const key of ["project", "path", "why"]) {
      if (typeof f?.[key] !== "string" || f[key].trim() === "") problems.push(`Risky file entry ${i} has no ${key}.`);
    }
  });
  return problems;
}

// True when a survivor in `file` of `project` sits in a risky area. A path matches at a folder boundary, so "Storage/" does not
// match "MyStorage/".
export function isRisky(risky, project, file) {
  if (Object.hasOwn(risky.projects ?? {}, project)) return true;
  const f = normalizeFile(file);
  return (risky.files ?? []).some((r) => {
    if (r.project !== project) return false;
    const p = normalizeFile(r.path);
    return f === p || f.startsWith(p) || f.includes(`/${p}`);
  });
}

export function summary(thresholds, equivalents, reports, scores) {
  const lines = ["## Mutation score per project", "", "| Project | Score | Threshold | Survivors |", "|---|---|---|---|"];
  for (const [name, s] of Object.entries(scores)) {
    const t = thresholds.projects?.[name]?.score;
    const open = unlistedSurvivors(equivalents, name, reports[name]).length;
    lines.push(`| ${name} | ${s === null ? "n/a" : `${floor2(s).toFixed(2)}%`} | ${t === undefined ? "none" : `${t}%`} | ${open} |`);
  }
  for (const [name, r] of Object.entries(reports)) {
    const open = unlistedSurvivors(equivalents, name, r);
    if (open.length === 0) continue;
    lines.push("", `### ${name}: surviving mutants (test gaps)`, "");
    for (const m of open.slice(0, 500)) {
      const to = String(m.replacement).split("\n").join(" ").slice(0, 80);
      lines.push(`- \`${m.file}:${m.line}\` ${m.mutator} -> \`${to}\` (${m.status})`);
    }
    if (open.length > 500) lines.push(`- ... and ${open.length - 500} more in the report.`);
  }
  return lines.join("\n");
}

function loadReports(dir) {
  const out = {};
  for (const f of readdirSync(dir).filter((x) => x.endsWith(".json")).sort()) {
    out[path.basename(f, ".json")] = JSON.parse(readFileSync(path.join(dir, f), "utf8"));
  }
  return out;
}

function parseArgs(argv) {
  const o = { thresholds: "docs/testing/mutation-thresholds.json", equivalents: "docs/testing/mutation-equivalents.json", risky: "docs/testing/mutation-risky-areas.json" };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === "--reports") o.reports = argv[++i];
    else if (a === "--thresholds") o.thresholds = argv[++i];
    else if (a === "--equivalents") o.equivalents = argv[++i];
    else if (a === "--risky") o.risky = argv[++i];
    else if (a === "--base") o.base = argv[++i];
    else if (a === "--update") o.update = true;
    else if (a === "--survivors") o.survivors = true;
    else if (a === "--summary") o.summary = true;
    else throw new Error(`Unknown argument ${a}`);
  }
  return o;
}

function main() {
  const o = parseArgs(process.argv.slice(2));
  const problems = [];
  const thresholds = existsSync(o.thresholds) ? JSON.parse(readFileSync(o.thresholds, "utf8")) : null;
  const equivalents = existsSync(o.equivalents) ? JSON.parse(readFileSync(o.equivalents, "utf8")) : null;
  const risky = existsSync(o.risky) ? JSON.parse(readFileSync(o.risky, "utf8")) : null;
  if (!risky) problems.push(`${o.risky} is missing.`);
  else problems.push(...validateRisky(risky));
  if (!thresholds) problems.push(`${o.thresholds} is missing.`);
  else problems.push(...validateThresholds(thresholds));
  if (!equivalents) problems.push(`${o.equivalents} is missing.`);
  else problems.push(...validateEquivalents(equivalents));
  const notes = [];

  if (o.base && thresholds && problems.length === 0) {
    let before = null;
    try {
      before = JSON.parse(execFileSync("git", ["show", `${o.base}:docs/testing/mutation-thresholds.json`], { encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] }));
    } catch {
      notes.push(`No thresholds file at ${o.base} (or ${o.base} cannot be read): the floor ratchet is skipped.`);
    }
    if (before) problems.push(...compareFloors(before, thresholds));
  }

  if (o.reports && problems.length === 0) {
    const reports = loadReports(o.reports);
    const scores = Object.fromEntries(Object.entries(reports).map(([name, r]) => [name, score(r)]));
    if (o.summary) {
      console.log(summary(thresholds, equivalents, reports, scores));
      return;
    }
    for (const [name, s] of Object.entries(scores)) {
      const left = survivors(reports[name]).length;
      console.log(`${name}: ${s === null ? "nothing measured" : `${floor2(s).toFixed(2)}%`}, ${left} survivor${left === 1 ? "" : "s"}`);
    }
    if (o.update) {
      const next = update(thresholds, scores);
      writeFileSync(o.thresholds, JSON.stringify(next, null, 2) + "\n");
      console.log(`Thresholds raised in ${o.thresholds}.`);
    } else if (o.survivors) {
      for (const [name, r] of Object.entries(reports)) {
        for (const m of unlistedSurvivors(equivalents, name, r)) {
          if (!isRisky(risky, name, m.file)) {
            notes.push(`${name}: ${m.file}:${m.line} ${m.mutator} ${m.status === "NoCoverage" ? "is not covered" : "survived"} (not a risky area, so it does not fail the run; the score is published and may only go up).`);
            continue;
          }
          problems.push(`${name}: ${m.file}:${m.line} ${m.mutator} -> ${JSON.stringify(m.replacement)} ${m.status === "NoCoverage" ? "is not covered by any test" : "survived"}. It is in a risky area: kill it with a test, or list it in mutation-equivalents.json with the reason it cannot be killed.`);
        }
      }
    } else {
      const result = compare(thresholds, scores);
      problems.push(...result.failures);
      notes.push(...result.notes);
    }
  }
  for (const n of notes) console.log(`note: ${n}`);
  if (problems.length > 0) {
    for (const p of problems) console.error(`FAIL: ${p}`);
    process.exit(1);
  }
  console.log(o.reports ? "Mutation checks hold." : "Mutation thresholds and equivalent-mutant list are valid.");
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    main();
  } catch (e) {
    console.error(e.message);
    process.exit(1);
  }
}
