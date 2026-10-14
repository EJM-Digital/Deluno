#!/usr/bin/env node
// Merges every coverage report into one per-file table (issue 1268, docs/testing/STRATEGY.md).
//
// Inputs
//   --cobertura <file>        a cobertura XML (coverlet, or ReportGenerator's merge of several); repeatable
//   --lcov <file>[=<prefix>]  an lcov file (vitest v8, node --test); repeatable. A relative path in the file gets
//                             <prefix> in front of it (vitest writes paths relative to apps/web).
//   --exclusions <file>       docs/testing/coverage-exclusions.json: the only place a file may be left out, each with a reason
//   --repo-root <dir>         absolute paths are made relative to this (default: the current directory)
// Outputs
//   --out-json <file>         the report the ratchet reads: per file, covered/total lines and branches
//   --out-md <file>           the same as a table, one row per production file
//   --write-doc <file>        rewrites the measured block of docs/testing/COVERAGE.md (between its coverage:measured markers)
//
// A file is a production file when it is in a report and not excluded. Nothing is dropped silently: an excluded file
// is listed, with its reason, in `excluded` in the JSON and in the table.
import { readFileSync, writeFileSync, mkdirSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { pathToFileURL } from "node:url";

export const DOC_START = "<!-- coverage:measured:start -->";
export const DOC_END = "<!-- coverage:measured:end -->";

const TOP_LEVEL = ["src", "apps", "sdk", "services", "scripts", "integrations", "installer", "tests"];

// A path as the repository spells it: forward slashes, relative to the repository root.
export function normalizePath(path, repoRoot = "", prefix = "") {
  let p = String(path).replace(/\\/g, "/");
  const root = repoRoot.replace(/\\/g, "/").replace(/\/+$/, "");
  const isAbsolute = /^([a-zA-Z]:)?\//.test(p);
  if (root && p.toLowerCase().startsWith(root.toLowerCase() + "/")) return p.slice(root.length + 1);
  if (isAbsolute) {
    const m = p.match(new RegExp(`(?:^|/)((?:${TOP_LEVEL.join("|")})/.*)$`));
    return m ? m[1] : p;
  }
  p = p.replace(/^\.\//, "");
  return prefix ? `${prefix.replace(/\\/g, "/").replace(/\/+$/, "")}/${p}` : p;
}

function newFile() {
  return { lines: new Map(), branches: new Map() };
}

function fileOf(files, name) {
  if (!files.has(name)) files.set(name, newFile());
  return files.get(name);
}

function addLine(file, number, hits) {
  file.lines.set(number, Math.max(file.lines.get(number) ?? 0, hits));
}

function addBranches(file, number, covered, total) {
  const old = file.branches.get(number);
  if (!old || total > old.total || (total === old.total && covered > old.covered)) {
    file.branches.set(number, { covered, total });
  }
}

function attrs(tag) {
  const out = {};
  for (const m of tag.matchAll(/([\w:-]+)="([^"]*)"/g)) out[m[1]] = m[2];
  return out;
}

export function parseCobertura(xml, { repoRoot = "" } = {}) {
  const files = new Map();
  const sources = [...xml.matchAll(/<source>([^<]*)<\/source>/g)].map((m) => m[1]);
  for (const cls of xml.matchAll(/<class\b([^>]*)>([\s\S]*?)<\/class>/g)) {
    const { filename } = attrs(cls[1]);
    if (!filename) continue;
    let name = filename.replace(/\\/g, "/");
    if (!/^([a-zA-Z]:)?\//.test(name) && sources.length > 0) {
      // Relative to a <source>: ReportGenerator's merged output.
      name = `${sources[0].replace(/\\/g, "/").replace(/\/+$/, "")}/${name}`;
    }
    const file = fileOf(files, normalizePath(name, repoRoot));
    for (const ln of cls[2].matchAll(/<line\b([^>]*?)\/?>/g)) {
      const a = attrs(ln[1]);
      const number = Number(a.number);
      if (!Number.isInteger(number)) continue;
      addLine(file, number, Number(a.hits) || 0);
      const cc = /\((\d+)\/(\d+)\)/.exec(a["condition-coverage"] ?? "");
      if (a.branch?.toLowerCase() === "true" && cc) addBranches(file, number, Number(cc[1]), Number(cc[2]));
    }
  }
  return files;
}

export function parseLcov(text, { repoRoot = "", prefix = "" } = {}) {
  const files = new Map();
  let file = null;
  const perLine = new Map();
  for (const raw of text.split(/\r?\n/)) {
    const line = raw.trim();
    if (line.startsWith("SF:")) {
      file = fileOf(files, normalizePath(line.slice(3), repoRoot, prefix));
      perLine.clear();
    } else if (line.startsWith("DA:") && file) {
      const [n, hits] = line.slice(3).split(",");
      addLine(file, Number(n), Number(hits) || 0);
    } else if (line.startsWith("BRDA:") && file) {
      const [n, , , taken] = line.slice(5).split(",");
      const entry = perLine.get(n) ?? { covered: 0, total: 0 };
      entry.total += 1;
      if (taken !== "-" && Number(taken) > 0) entry.covered += 1;
      perLine.set(n, entry);
    } else if (line === "end_of_record" && file) {
      for (const [n, e] of perLine) addBranches(file, Number(n), e.covered, e.total);
      perLine.clear();
      file = null;
    }
  }
  return files;
}

export function mergeFiles(...maps) {
  const out = new Map();
  for (const map of maps) {
    for (const [name, f] of map) {
      const target = fileOf(out, name);
      for (const [n, hits] of f.lines) addLine(target, n, hits);
      for (const [n, b] of f.branches) addBranches(target, n, b.covered, b.total);
    }
  }
  return out;
}

function globToRegExp(glob) {
  let re = "";
  for (let i = 0; i < glob.length; i++) {
    const c = glob[i];
    if (c === "*" && glob[i + 1] === "*") {
      re += ".*";
      i++;
      if (glob[i + 1] === "/") i++;
    } else if (c === "*") re += "[^/]*";
    else if (c === "?") re += "[^/]";
    else re += c.replace(/[.+^${}()|[\]\\]/g, "\\$&");
  }
  return new RegExp(`^${re}$`);
}

// Returns the exclusion (with its reason) that covers `file`, or undefined.
export function findExclusion(exclusions, file) {
  return exclusions.find((e) => typeof e.path === "string" && globToRegExp(e.path).test(file));
}

const tally = (covered, total) => ({ covered, total });

export function buildReport(files, exclusions = []) {
  const out = { version: 1, totals: {}, areas: {}, files: {}, excluded: [] };
  const sums = { lines: tally(0, 0), branches: tally(0, 0) };
  const areaSums = new Map();
  for (const name of [...files.keys()].sort()) {
    const f = files.get(name);
    const excluded = findExclusion(exclusions, name);
    if (excluded) {
      out.excluded.push({ file: name, reason: excluded.reason ?? "" });
      continue;
    }
    const lines = tally([...f.lines.values()].filter((h) => h > 0).length, f.lines.size);
    const branches = tally(
      [...f.branches.values()].reduce((s, b) => s + b.covered, 0),
      [...f.branches.values()].reduce((s, b) => s + b.total, 0)
    );
    out.files[name] = { lines, branches };
    const area = areaOf(name);
    const a = areaSums.get(area) ?? { lines: tally(0, 0), branches: tally(0, 0), files: 0 };
    for (const [target, v] of [[sums, { lines, branches }], [a, { lines, branches }]]) {
      target.lines.covered += v.lines.covered;
      target.lines.total += v.lines.total;
      target.branches.covered += v.branches.covered;
      target.branches.total += v.branches.total;
    }
    a.files += 1;
    areaSums.set(area, a);
  }
  out.totals = sums;
  for (const area of [...areaSums.keys()].sort()) out.areas[area] = areaSums.get(area);
  return out;
}

export function areaOf(file) {
  const parts = file.split("/");
  if (["src", "apps", "services", "sdk", "integrations"].includes(parts[0]) && parts.length > 2) return `${parts[0]}/${parts[1]}`;
  return parts[0];
}

export function pct(part) {
  return part.total === 0 ? null : (part.covered / part.total) * 100;
}

const show = (p) => (p === null ? "n/a" : `${p.toFixed(1)}%`);
const frac = (part) => `${part.covered}/${part.total}`;

export function renderTable(report) {
  const rows = ["| File | Line | Branch | Lines | Branches |", "| --- | ---: | ---: | ---: | ---: |"];
  for (const [name, f] of Object.entries(report.files)) {
    rows.push(`| ${name} | ${show(pct(f.lines))} | ${show(pct(f.branches))} | ${frac(f.lines)} | ${frac(f.branches)} |`);
  }
  rows.push("", `Total: line ${show(pct(report.totals.lines))} (${frac(report.totals.lines)}), branch ${show(pct(report.totals.branches))} (${frac(report.totals.branches)}).`);
  if (report.excluded.length > 0) {
    rows.push("", "Excluded by docs/testing/coverage-exclusions.json:", "");
    for (const e of report.excluded) rows.push(`- ${e.file}: ${e.reason}`);
  }
  return rows.join("\n") + "\n";
}

export function renderDocBlock(report) {
  if (!report) {
    return [
      "No measured report yet. CI's `coverage` job writes the first one (issue 1268); until then this block is a placeholder.",
      "",
      "| Area | Line | Branch | Lines | Branches | Files |",
      "| --- | ---: | ---: | ---: | ---: | ---: |",
      "| not measured yet | n/a | n/a | 0/0 | 0/0 | 0 |"
    ].join("\n");
  }
  const rows = [
    "Generated by `node scripts/coverage/merge-coverage.mjs --write-doc` from the merged CI report. Do not edit by hand.",
    "",
    "| Area | Line | Branch | Lines | Branches | Files |",
    "| --- | ---: | ---: | ---: | ---: | ---: |"
  ];
  for (const [area, a] of Object.entries(report.areas)) {
    rows.push(`| ${area} | ${show(pct(a.lines))} | ${show(pct(a.branches))} | ${frac(a.lines)} | ${frac(a.branches)} | ${a.files} |`);
  }
  const t = report.totals;
  rows.push(`| **All** | **${show(pct(t.lines))}** | **${show(pct(t.branches))}** | ${frac(t.lines)} | ${frac(t.branches)} | ${Object.keys(report.files).length} |`);
  return rows.join("\n");
}

export function writeDocBlock(markdown, report) {
  const start = markdown.indexOf(DOC_START);
  const end = markdown.indexOf(DOC_END);
  if (start < 0 || end < start) throw new Error(`COVERAGE.md has no ${DOC_START} ... ${DOC_END} block.`);
  return `${markdown.slice(0, start + DOC_START.length)}\n${renderDocBlock(report)}\n${markdown.slice(end)}`;
}

function parseArgs(argv) {
  const o = { cobertura: [], lcov: [], repoRoot: process.cwd() };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    const next = () => argv[++i];
    if (a === "--cobertura") o.cobertura.push(next());
    else if (a === "--lcov") o.lcov.push(next());
    else if (a === "--exclusions") o.exclusions = next();
    else if (a === "--repo-root") o.repoRoot = resolve(next());
    else if (a === "--out-json") o.outJson = next();
    else if (a === "--out-md") o.outMd = next();
    else if (a === "--write-doc") o.writeDoc = next();
    else throw new Error(`Unknown argument ${a}`);
  }
  return o;
}

function main() {
  const o = parseArgs(process.argv.slice(2));
  const maps = [];
  for (const f of o.cobertura) maps.push(parseCobertura(readFileSync(f, "utf8"), { repoRoot: o.repoRoot }));
  for (const spec of o.lcov) {
    const eq = spec.indexOf("=");
    const file = eq < 0 ? spec : spec.slice(0, eq);
    const prefix = eq < 0 ? "" : spec.slice(eq + 1);
    maps.push(parseLcov(readFileSync(file, "utf8"), { repoRoot: o.repoRoot, prefix }));
  }
  if (maps.length === 0) throw new Error("Give at least one --cobertura or --lcov report.");
  const exclusions = o.exclusions ? JSON.parse(readFileSync(o.exclusions, "utf8")).exclusions ?? [] : [];
  const report = buildReport(mergeFiles(...maps), exclusions);
  const write = (path, text) => {
    mkdirSync(dirname(resolve(path)), { recursive: true });
    writeFileSync(path, text);
  };
  if (o.outJson) write(o.outJson, JSON.stringify(report, null, 2) + "\n");
  if (o.outMd) write(o.outMd, renderTable(report));
  if (o.writeDoc) write(o.writeDoc, writeDocBlock(readFileSync(o.writeDoc, "utf8"), report));
  console.log(`Merged ${Object.keys(report.files).length} production files, ${report.excluded.length} excluded.`);
  console.log(`Line ${show(pct(report.totals.lines))}, branch ${show(pct(report.totals.branches))}.`);
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    main();
  } catch (e) {
    console.error(e.message);
    process.exit(1);
  }
}
