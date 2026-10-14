// The coverage register (docs/testing/coverage-register.json) must account for everything Deluno does, and every proof it names must exist.
//
//   node scripts/check-coverage-register.mjs                run the checks; (a) and (b) fail, (c) and the determinism counts are reported
//   node scripts/check-coverage-register.mjs --strict       also fail on any gap (a row whose proofs do not reach its required level) and any rule breach
//   node scripts/check-coverage-register.mjs --stubs        print a register row stub for every inventory item without a row
//   node scripts/check-coverage-register.mjs --write-doc    rewrite the generated totals in docs/testing/COVERAGE.md
//   node scripts/check-coverage-register.mjs --json         print the totals as JSON
//   node scripts/check-coverage-register.mjs --list-gaps    print every gap, high first
//
// (a) every inventory item (scripts/coverage/inventory.mjs: route, web route, hosted service, job lane/handler/task, health check,
//     migration, integration client and operation, tray action, golden-path scenario) is named in the `covers` of some row;
// (b) every proof a row names ({ type, file, name }) exists: the file is in the repository and the name is a test, a group or a class in it
//     (name "*" means the whole file); a scenario proof names an id in ScenarioPlan.ps1;
// (c) every row's proofs reach its required level. A high row that touches another app, files on disk or install/update/tray (its
//     `touches` is other-app, disk or install) needs "real": a golden-VM scenario, a test that replays the other app's recorded real reply,
//     a "real-fs" test (disk and install rows
//     only, never a row marked "vm": true; see below), or, for a screen behaviour, a Playwright smoke spec whose API calls are not stubbed (decided statically; undecidable counts as stubbed).
//     Every other row needs at least "inproc". Reported as a total; only --strict fails on it.
//     real-fs: a backend test that runs Deluno's real code against real files in a real temp folder (real hard links, moves, deletes, permissions)
//     or a real SQLite database file, with no fake or in-memory file system or store. The checker decides it statically (the class setup or the test
//     creates the temp folder or database, does file or database work, and names no fake); if it cannot decide, the claim is refused: it counts as
//     inproc and is counted in the totals. A row marked "vm": true can only be proven by a real install (NTFS-specific behaviour, network shares,
//     cross-volume moves and links, the installer, the update, the tray, Windows service mode): it needs a scenario proof, nothing else reaches real.
// Only (a) and (b) fail the default run. A malformed register, a stale cover, a proof of the wrong type and the rules below are reported.
// Determinism rules, reported with counts and failing under --strict: no fixed sleep in a test without a `reason:` comment on its line;
// every golden-path scenario runnable alone; every external-app test on a recording or in tests/recorded-reply-allow-list.json.
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { TestIndex, read, walk, slash } from './coverage/testindex.mjs';
import { buildInventory, countsOf, scenarioPlan } from './coverage/inventory.mjs';

export const PROOF_TYPES = ['vitest', 'smoke', 'backend', 'scenario', 'recording-replay', 'real-fs'];
export const KINDS = ['screen action', 'state', 'API route', 'job', 'integration-operation', 'failure-mode', 'migration', 'install-update', 'tray'];
export const SEVERITIES = ['high', 'medium', 'low'];
export const REAL_TYPES = new Set(['scenario', 'recording-replay']);
/** real-fs satisfies "real" only for these rows, and never for a row marked "vm": true. */
export const REAL_FS_TOUCHES = new Set(['disk', 'install']);
export const TOUCHES = ['other-app', 'disk', 'install', 'none'];
/** Areas whose rows are behaviours of a screen: a smoke spec against the real host may reach "real" for these. */
export const SCREEN_AREAS = new Set(['shell', 'dashboard', 'guided-setup', 'library', 'movie-detail', 'show-detail', 'search-grab', 'queue', 'activity', 'connections', 'settings-library', 'settings-lists', 'automation', 'system', 'web']);
const LEVEL_NAMES = ['none', 'inproc', 'real'];

/** A high row that touches another app, files on disk, or install/update/tray needs real proof; every other row at least inproc. */
export const requiredFor = (row) => (row.severity === 'high' && row.touches !== 'none' ? 'real' : 'inproc');
export const requiredLevel = (row) => (requiredFor(row) === 'real' ? 2 : 1);

/** Is this smoke proof stubbed? Decided statically; what cannot be decided counts as stubbed. */
export function smokeState(proof, index) {
  if (!index || typeof index.stubInfo !== 'function') return { stubbed: true, decided: false };
  return index.stubInfo(proof.file, proof.name);
}
/** Does a real-fs proof count for this row? Only for a disk or install row that is not "vm", and only when the checker verifies the claim statically. */
export function realFsCounts(proof, row, index) {
  if (proof.type !== 'real-fs' || !row || row.vm === true || !REAL_FS_TOUCHES.has(row.touches)) return false;
  return !!index && typeof index.realFs === 'function' && index.realFs(proof.file, proof.name).real;
}
/**
 * scenario and recording-replay are real; real-fs is real for a verified disk or install row that is not "vm"; a smoke spec is real only for a
 * screen behaviour and only when its API calls are not stubbed. A "vm" row (only a real install can prove it) is real by a scenario alone.
 */
export function proofLevel(proof, row, index) {
  if (row && row.vm === true) return proof.type === 'scenario' ? 2 : 1;
  if (proof.type === 'real-fs') return realFsCounts(proof, row, index) ? 2 : 1;
  if (REAL_TYPES.has(proof.type)) return 2;
  if (proof.type === 'smoke' && row && SCREEN_AREAS.has(row.area) && !smokeState(proof, index).stubbed) return 2;
  return 1;
}
export const rowLevel = (row, index) => (row.proofs ?? []).reduce((best, p) => Math.max(best, proofLevel(p, row, index)), 0);
export const isGap = (row, index) => rowLevel(row, index) < requiredLevel(row);

/** The register's own shape: ids, enums, and the rule deriving the required level from severity and what the row touches. */
export function validateShape(register) {
  const problems = [];
  const areas = new Set(Object.keys(register.areas ?? {}));
  const ids = new Set();
  for (const row of register.rows ?? []) {
    const where = `row ${row.id ?? '(no id)'}`;
    if (!row.id || typeof row.id !== 'string') problems.push(`${where}: needs an id`);
    else if (ids.has(row.id)) problems.push(`${where}: id used twice`);
    else ids.add(row.id);
    if (!areas.has(row.area)) problems.push(`${where}: area "${row.area}" is not in the register's areas`);
    if (!row.behaviour || !String(row.behaviour).trim()) problems.push(`${where}: needs a behaviour`);
    if (!KINDS.includes(row.kind)) problems.push(`${where}: kind "${row.kind}" is not one of ${KINDS.join(', ')}`);
    if (!SEVERITIES.includes(row.severity)) problems.push(`${where}: severity "${row.severity}" is not high, medium or low`);
    if (!TOUCHES.includes(row.touches)) problems.push(`${where}: touches "${row.touches}" is not one of ${TOUCHES.join(', ')}`);
    else if (row.required !== requiredFor(row)) problems.push(`${where}: required must be "${requiredFor(row)}" for a ${row.severity} row that touches ${row.touches}`);
    if (row.vm != null && row.vm !== true) problems.push(`${where}: vm may only be true (leave it out otherwise)`);
    if (row.vm === true && !REAL_FS_TOUCHES.has(row.touches)) problems.push(`${where}: vm is for a row that touches disk or install, not ${row.touches}`);
    if (row.issue != null && !(Number.isInteger(row.issue) && row.issue > 0)) problems.push(`${where}: issue must be the number of an open coverage issue`);
    if (!Array.isArray(row.proofs)) problems.push(`${where}: proofs must be a list (empty when it is a gap)`);
    for (const p of [...(row.proofs ?? []), ...(row.partial ?? [])]) {
      if (!PROOF_TYPES.includes(p.type)) problems.push(`${where}: proof type "${p.type}" is not one of ${PROOF_TYPES.join(', ')}`);
      if (!p.file || !p.name) problems.push(`${where}: a proof needs a file and a name`);
    }
  }
  return problems;
}

/** (a): inventory items no row covers, and covers naming an item that is gone. */
export function mapInventory(register, inventory, { checkStale = true, ignoreStale = [] } = {}) {
  const keys = new Set(inventory.map((i) => i.key));
  const covered = new Set();
  for (const row of register.rows) for (const k of row.covers ?? []) covered.add(k);
  const unmapped = inventory.filter((i) => !covered.has(i.key));
  const stale = [];
  if (checkStale) for (const row of register.rows) for (const k of row.covers ?? []) if (!keys.has(k) && !ignoreStale.some((p) => k.startsWith(p))) stale.push({ row: row.id, key: k });
  return { unmapped, stale };
}

/** A proof's type must be true of its file: a "recording-replay" test must load a recorded reply, a "scenario" is a PowerShell scenario script. */
function typeMismatch(p, index) {
  switch (p.type) {
    case 'scenario': return p.file.endsWith('.ps1') ? null : `a scenario proof must be a .ps1 scenario script, not ${p.file}`;
    case 'smoke': return /^apps\/web\/tests\/.+\.spec\.ts$/.test(p.file) ? null : `a smoke proof must be a Playwright spec in apps/web/tests, not ${p.file}`;
    case 'vitest': return /\.test\.tsx?$/.test(p.file) ? null : `a vitest proof must be a .test.ts(x) file, not ${p.file}`;
    case 'real-fs': {
      if (!p.file.endsWith('.cs')) return `a real-fs proof must be a C# backend test, not ${p.file}`;
      const r = index.realFs(p.file, p.name);
      return r.real ? null : `real-fs refused (counts as inproc): ${r.why}`;
    }
    case 'recording-replay': return index.replaysRecording(p.file, p.name) ? null : `"${p.name}" in ${p.file} does not load a recorded real reply (RecordedReplies / the Weir contract helper)`;
    default: return p.file.endsWith('.ps1') ? `a backend proof cannot be a PowerShell script (${p.file}); use type "scenario"` : null;
  }
}

/** (b): a proof whose file or test is gone, or whose type is not true of it. */
export function missingProofs(register, index, planIds) {
  const bad = [];
  const plan = planIds ? new Set(planIds) : null;
  for (const row of register.rows) {
    for (const [list, label] of [[row.proofs ?? [], 'proof'], [row.partial ?? [], 'partial proof']]) {
      for (const p of list) {
        if (!index.has(p.file)) { bad.push({ kind: 'missing', row: row.id, label, proof: p, why: `the file ${p.file} does not exist` }); continue; }
        const typeProblem = typeMismatch(p, index);
        if (typeProblem) { bad.push({ kind: 'type', row: row.id, label, proof: p, why: typeProblem }); continue; }
        if (p.name === '*') continue;
        if (p.type === 'scenario' && plan && plan.has(p.name)) continue;
        if (!index.hasName(p.file, p.name)) bad.push({ kind: 'missing', row: row.id, label, proof: p, why: `no test, group or class "${p.name}" in ${p.file}` });
      }
    }
  }
  return bad;
}

/** (c): totals. */
export function totals(register, index) {
  const byArea = {};
  const bySeverity = { high: blank(), medium: blank(), low: blank() };
  const byLevel = { none: 0, inproc: 0, real: 0 };
  const all = blank();
  const gaps = [];
  const highByTouches = Object.fromEntries(TOUCHES.map((t) => [t, { rows: 0, gaps: 0 }]));
  const smoke = { proofs: 0, unstubbed: 0, stubbed: 0, undecided: 0 };
  const realFs = { claims: 0, counted: 0, refused: 0, noEffect: 0, vmRows: 0, vmRowGaps: 0 };
  function blank() { return { rows: 0, proven: 0, gaps: 0 }; }
  for (const row of register.rows) {
    const gap = isGap(row, index);
    if (row.severity === 'high' && highByTouches[row.touches]) { highByTouches[row.touches].rows++; if (gap) highByTouches[row.touches].gaps++; }
    for (const pr of row.proofs ?? []) if (pr.type === 'smoke') { const st = smokeState(pr, index); smoke.proofs++; if (!st.stubbed) smoke.unstubbed++; else if (st.decided) smoke.stubbed++; else smoke.undecided++; }
    if (row.vm === true) { realFs.vmRows++; if (gap) realFs.vmRowGaps++; }
    for (const pr of row.proofs ?? []) if (pr.type === 'real-fs') {
      realFs.claims++;
      if (!(index && typeof index.realFs === 'function' && index.realFs(pr.file, pr.name).real)) realFs.refused++;
      else if (realFsCounts(pr, row, index)) realFs.counted++;
      else realFs.noEffect++;
    }
    const a = (byArea[row.area] ??= { ...blank(), high: 0, highGaps: 0, level: { none: 0, inproc: 0, real: 0 } });
    for (const t of [a, bySeverity[row.severity], all]) { t.rows++; if (gap) t.gaps++; else t.proven++; }
    a.high += row.severity === 'high' ? 1 : 0;
    if (gap && row.severity === 'high') a.highGaps++;
    const lv = LEVEL_NAMES[rowLevel(row, index)];
    a.level[lv]++;
    byLevel[lv]++;
    if (gap) gaps.push(row);
  }
  return { all, byArea, bySeverity, byLevel, highByTouches, smoke, realFs, gaps, highGaps: gaps.filter((g) => g.severity === 'high').length };
}

// ------------------------------------------------------------------------------------------------ determinism rules

const SLEEPS = [
  { files: (f) => f.endsWith('.cs') && f.startsWith('tests/'), re: /\b(Task\.Delay|Thread\.Sleep)\s*\(/ },
  { files: (f) => /^apps\/web\/(tests|src)\//.test(f) && /\.(spec|test)\.tsx?$/.test(f), re: /\bwaitForTimeout\s*\(/ },
  { files: (f) => /^scripts\/golden-path\/.+\.ps1$/.test(f), re: /\bStart-Sleep\b/ }
];
const REASON = /(\/\/|#)\s*reason:/i;

/** Fixed sleeps in tests that carry no `reason:` comment on their line. */
export function sleepViolations(index) {
  const out = [];
  for (const [file, f] of index.files) {
    const rules = SLEEPS.filter((s) => s.files(file));
    if (rules.length === 0) continue;
    const lines = f.text.split(/\r?\n/);
    for (let i = 0; i < lines.length; i++) {
      const line = lines[i];
      if (!rules.some((r) => r.re.test(line)) || REASON.test(line)) continue;
      // A commented-out call is not a sleep.
      if (/^\s*(\/\/|#)/.test(line)) continue;
      out.push({ file, line: i + 1, text: line.trim().slice(0, 120) });
    }
  }
  return out;
}

/** A scenario is runnable alone when it is registered under its own id in ScenarioPlan.ps1 and its group function exists. */
export function scenarioViolations(plan, index) {
  const out = [];
  const seen = new Set();
  const scenarioText = [...index.files].filter(([f]) => /scenarios\/G\d+/.test(f)).map(([, v]) => v.text).join('\n');
  const functions = new Set([...scenarioText.matchAll(/^\s*function\s+([\w-]+)/gim)].map((m) => m[1].toLowerCase()));
  for (const { id, fn, group } of plan) {
    if (seen.has(id)) out.push({ id, why: 'id registered twice in ScenarioPlan.ps1' });
    seen.add(id);
    if (!/^[a-z][a-z0-9-]*(\.[a-z0-9-]+)*$/.test(id)) out.push({ id, why: 'id is not a plain dotted lower-case id, so -Scenarios cannot pick it' });
    if (fn && !functions.has(fn.toLowerCase())) out.push({ id, why: `group function ${fn} is not defined in the scenario scripts` });
    if (!fn && !/^(githubpicker|update)$/.test(group)) out.push({ id, why: `group ${group} has no function and is not a host group` });
  }
  return out;
}

// ------------------------------------------------------------------------------------------------------- the report

export function evaluate({ register, inventory, index, plan, root }) {
  const internal = [];
  const safe = (label, fn, fallback) => {
    try { return fn(); } catch (error) { internal.push(`${label}: ${error?.message ?? error}`); return fallback; }
  };
  const shape = safe('register shape', () => validateShape(register), []);
  // Without PowerShell the golden-path plan cannot be read: its scenario rows are then neither required nor judged stale.
  const { unmapped, stale } = mapInventory(register, inventory, { checkStale: true, ignoreStale: plan.length ? [] : ['scenario:'] });
  const planIds = plan.length ? plan.map((p) => p.id) : null;
  const proofProblems = missingProofs(register, index, planIds);
  const missing = proofProblems.filter((m) => m.kind === 'missing');
  const typeProblems = proofProblems.filter((m) => m.kind === 'type');
  const t = safe('totals', () => totals(register, index), { all: { rows: 0, proven: 0, gaps: 0 }, byArea: {}, bySeverity: { high: { rows: 0, proven: 0, gaps: 0 }, medium: { rows: 0, proven: 0, gaps: 0 }, low: { rows: 0, proven: 0, gaps: 0 } }, byLevel: { none: 0, inproc: 0, real: 0 }, highByTouches: {}, smoke: { proofs: 0, unstubbed: 0, stubbed: 0, undecided: 0 }, realFs: { claims: 0, counted: 0, refused: 0, noEffect: 0, vmRows: 0, vmRowGaps: 0 }, gaps: [], highGaps: 0 });
  const sleeps = safe('sleep rule', () => sleepViolations(index), []);
  const scenarios = safe('scenario rule', () => (plan.length ? scenarioViolations(plan, index) : []), []);
  return { shape, unmapped, stale, missing, typeProblems, totals: t, sleeps, scenarios, internal };
}

async function externalAppViolations(root) {
  try {
    const file = join(root, 'scripts/check-recorded-replies.mjs');
    if (!existsSync(file)) return { problems: [], offenders: new Map(), listed: 0 };
    const m = await import(pathToFileURL(file).href);
    const { problems, offenders } = m.check(root);
    return { problems, offenders, listed: offenders.size };
  } catch (error) {
    return { problems: [], offenders: new Map(), listed: 0, error: String(error?.message ?? error) };
  }
}

const START = '<!-- coverage:generated:start -->';
const END = '<!-- coverage:generated:end -->';

export function generatedSection(register, inventory, t) {
  const pct = (a, b) => (b ? `${Math.round((a / b) * 100)}%` : '-');
  const lines = [];
  lines.push(`Generated by \`node scripts/check-coverage-register.mjs --write-doc\` from \`coverage-register.json\`. Do not edit by hand.`, '');
  lines.push(`**${t.all.rows}** behaviours. **${t.all.proven}** reach their required proof level (${pct(t.all.proven, t.all.rows)}); **${t.all.gaps}** are gaps, **${t.highGaps}** of them high.`, '');
  lines.push('### By severity', '', '| Severity | Rows | Reach their level | Gaps |', '| --- | ---: | ---: | ---: |');
  for (const s of SEVERITIES) lines.push(`| ${s} | ${t.bySeverity[s].rows} | ${t.bySeverity[s].proven} | ${t.bySeverity[s].gaps} |`);
  lines.push('', '### High rows by what they touch (these need `real`, except `none`, which needs `inproc`)', '', '| Touches | High rows | High gaps |', '| --- | ---: | ---: |');
  for (const k of TOUCHES) lines.push(`| ${k} | ${t.highByTouches[k]?.rows ?? 0} | ${t.highByTouches[k]?.gaps ?? 0} |`);
  lines.push('', '### By best proof a row has', '', '| Best proof | Rows |', '| --- | ---: |');
  for (const l of LEVEL_NAMES) lines.push(`| ${l === 'none' ? 'none (a gap)' : l} | ${t.byLevel[l]} |`);
  lines.push('', `Smoke proofs: **${t.smoke.proofs}**; unstubbed (can reach \`real\` for a screen behaviour) **${t.smoke.unstubbed}**; stubbed **${t.smoke.stubbed}**; not decidable statically, counted as stubbed **${t.smoke.undecided}**.`);
  lines.push('', `Real-fs proofs: **${t.realFs.claims}** claimed; **${t.realFs.counted}** verified and counted as real; **${t.realFs.refused}** refused (the checker could not verify a real temp folder or database file with no fake, so they count as inproc); **${t.realFs.noEffect}** verified but without effect (the row is "vm", or does not touch disk or install). **${t.realFs.vmRows}** rows are "vm" (only a real install can prove them, so they need a scenario); **${t.realFs.vmRowGaps}** of them are still gaps.`);
  lines.push('', '### By area', '', '| Area | What | Rows | Reach their level | Gaps | High rows | High gaps | No proof | Inproc | Real |', '| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |');
  for (const [area, a] of Object.entries(t.byArea).sort()) {
    lines.push(`| ${area} | ${register.areas[area]?.name ?? ''} | ${a.rows} | ${a.proven} | ${a.gaps} | ${a.high} | ${a.highGaps} | ${a.level.none} | ${a.level.inproc} | ${a.level.real} |`);
  }
  const byIssue = new Map();
  for (const row of register.rows) if (row.issue != null) {
    const e = byIssue.get(row.issue) ?? { rows: 0, gaps: 0, high: 0 };
    e.rows++;
    if (t.gaps.includes(row)) { e.gaps++; if (row.severity === 'high') e.high++; }
    byIssue.set(row.issue, e);
  }
  lines.push('', '### Gaps with an open issue', '');
  if (byIssue.size === 0) lines.push('No row names an issue yet.');
  else {
    lines.push('| Issue | Rows | Still gaps | High gaps |', '| --- | ---: | ---: | ---: |');
    for (const [issue, e] of [...byIssue].sort((a, b) => a[0] - b[0])) lines.push(`| #${issue} | ${e.rows} | ${e.gaps} | ${e.high} |`);
  }
  const inv = countsOf(inventory);
  lines.push('', '### Inventories (generated from source by `scripts/coverage/inventory.mjs`)', '', '| Kind | Items |', '| --- | ---: |');
  for (const [k, n] of Object.entries(inv)) lines.push(`| ${k} | ${n} |`);
  return lines.join('\n');
}

export function writeDoc(root, register, inventory, t) {
  const file = join(root, 'docs/testing/COVERAGE.md');
  const text = existsSync(file) ? read(file) : '';
  const section = `${START}\n${generatedSection(register, inventory, t)}\n${END}`;
  const next = text.includes(START) && text.includes(END)
    ? text.replace(new RegExp(`${START}[\\s\\S]*?${END}`), () => section)
    : `${text.trimEnd()}\n\n${section}\n`;
  writeFileSync(file, next);
  return file;
}

const stubFor = (item) => ({
  id: `${item.kind}.${item.key.split(':')[1].replace(/[^A-Za-z0-9]+/g, '-').toLowerCase()}`,
  area: '<area>',
  behaviour: `<one plain sentence about ${item.label ?? item.key}>`,
  kind: { route: 'API route', webroute: 'screen action', service: 'job', lane: 'job', handler: 'job', task: 'job', healthcheck: 'job', migration: 'migration', integration: 'integration-operation', tray: 'tray', scenario: 'integration-operation' }[item.kind],
  severity: 'medium',
  touches: 'none',
  required: 'inproc',
  proofs: [],
  covers: [item.key]
});

async function main() {
  const args = process.argv.slice(2);
  const rootArg = args.indexOf('--root');
  const root = resolve(rootArg >= 0 ? args[rootArg + 1] : join(dirname(fileURLToPath(import.meta.url)), '..'));
  const strict = args.includes('--strict');
  const registerPath = join(root, 'docs/testing/coverage-register.json');
  let register;
  try {
    register = JSON.parse(read(registerPath));
    if (!Array.isArray(register.rows)) throw new Error('no "rows" list');
  } catch (error) {
    console.error(`docs/testing/coverage-register.json cannot be read (${error.message}): every inventory item is unmapped.`);
    process.exit(1);
  }
  let index, inventory, planResult, plan, result;
  try {
    index = new TestIndex(root);
    planResult = scenarioPlan(root);
    plan = planResult.ids;
    inventory = buildInventory(root, { skipScenarios: plan.length === 0 });
    result = evaluate({ register, inventory, index, plan, root });
  } catch (error) {
    // The check itself broke. Only an unmapped item or a missing proof may fail CI, so say so and stop.
    console.error(`Coverage register check could not run: ${error?.stack ?? error}`);
    process.exit(strict ? 1 : 0);
  }
  const external = await externalAppViolations(root);
  const t = result.totals;

  if (args.includes('--stubs')) {
    console.log(JSON.stringify(result.unmapped.map(stubFor), null, 2));
    return;
  }
  if (args.includes('--write-doc')) {
    console.log('wrote ' + writeDoc(root, register, inventory, t));
    return;
  }
  if (args.includes('--json')) {
    console.log(JSON.stringify({ rows: t.all, bySeverity: t.bySeverity, highByTouches: t.highByTouches, smoke: t.smoke, realFs: t.realFs, byLevel: t.byLevel, byArea: t.byArea, highGaps: t.highGaps, inventory: countsOf(inventory) }, null, 1));
    return;
  }
  if (args.includes('--list-gaps')) {
    const order = { high: 0, medium: 1, low: 2 };
    for (const g of [...t.gaps].sort((a, b) => order[a.severity] - order[b.severity] || a.id.localeCompare(b.id))) console.log(`${g.severity.padEnd(6)} ${g.touches.padEnd(9)} ${g.id}  ${g.behaviour.slice(0, 100)}`);
    return;
  }

  const lines = [];
  const fail = []; // the only things that fail CI: (a) and (b)
  const warn = []; // everything else is reported, and fails only under --strict
  const shown = (list, f, n = 10) => list.slice(0, n).map(f).join('\n    ') + (list.length > n ? `\n    ... and ${list.length - n} more` : '');
  lines.push(`Coverage register: ${register.rows.length} rows, ${inventory.length} inventory items (${Object.entries(countsOf(inventory)).map(([k, n]) => `${n} ${k}`).join(', ')}).`);
  if (planResult.error) lines.push(`  !! ${planResult.error}: scenario ids are not checked.`);

  if (result.unmapped.length) fail.push(`(a) ${result.unmapped.length} inventory item(s) have no register row. Add a row with the key in "covers" (node scripts/check-coverage-register.mjs --stubs prints stubs):\n    ${shown(result.unmapped, (i) => `${i.key}  (${i.file ?? ''})`)}`);
  else lines.push('  OK  (a) every inventory item has a register row');
  if (result.missing.length) fail.push(`(b) ${result.missing.length} proof(s) name a file or test that does not exist (fix or remove the proof; the row is then a gap):\n    ${shown(result.missing, (m) => `${m.row}: ${m.why}`)}`);
  else lines.push('  OK  (b) every proof names a file and a test that exist');

  const notes = [
    [result.shape.length, 'register shape problem(s)', result.shape],
    [result.stale.length, 'row(s) cover an inventory item that no longer exists', result.stale.map((s) => `${s.row} -> ${s.key}`)],
    [result.typeProblems.length, 'proof(s) claim a type they do not have', result.typeProblems.map((m) => `${m.row}: ${m.why}`)],
    [result.internal.length, 'internal problem(s) in the check', result.internal],
    [external.error ? 1 : 0, 'problem reading the recorded-reply check', [external.error]]
  ];
  for (const [n, text, items] of notes) {
    if (!n) continue;
    lines.push(`  !!  ${n} ${text} (reported only):\n    ${shown(items, (x) => x, 5)}`);
    warn.push(`${n} ${text}`);
  }

  const levelLine = `(c) ${t.gaps.length} gaps (${t.highGaps} high): ${t.all.proven} of ${t.all.rows} rows reach their required proof level (high rows that touch another app, files on disk or install/update/tray need real; the rest inproc).`;
  lines.push(`  ${t.gaps.length ? '!!' : 'OK'}  ${levelLine}`);
  lines.push(`      by severity: ${SEVERITIES.map((s) => `${s} ${t.bySeverity[s].gaps}/${t.bySeverity[s].rows}`).join(', ')} gaps; high gaps by touches: ${TOUCHES.map((k) => `${k} ${t.highByTouches[k]?.gaps ?? 0}/${t.highByTouches[k]?.rows ?? 0}`).join(', ')}.`);
  lines.push(`      best proof: ${LEVEL_NAMES.map((l) => `${l} ${t.byLevel[l]}`).join(', ')}. smoke proofs ${t.smoke.proofs}: ${t.smoke.unstubbed} unstubbed, ${t.smoke.stubbed} stubbed, ${t.smoke.undecided} not decidable (counted stubbed).`);
  lines.push(`      real-fs proofs ${t.realFs.claims}: ${t.realFs.counted} counted, ${t.realFs.refused} refused (counted inproc), ${t.realFs.noEffect} without effect. vm rows (need a scenario): ${t.realFs.vmRows}, ${t.realFs.vmRowGaps} still gaps.`);
  if (t.gaps.length) warn.push(levelLine);

  const rules = [
    [result.sleeps.length, `${result.sleeps.length} fixed sleep(s) in tests without a "reason:" comment on the line`, result.sleeps.map((s) => `${s.file}:${s.line}`), 'no fixed sleep in a test without a reason'],
    [result.scenarios.length, `${result.scenarios.length} golden-path scenario(s) not runnable alone`, result.scenarios.map((s) => `${s.id}: ${s.why}`), `every golden-path scenario is runnable alone (${plan.length} ids)`],
    [external.problems.length, `${external.problems.length} external-app test problem(s): a reply built inline and not in tests/recorded-reply-allow-list.json (${external.listed} files listed there)`, external.problems, `every external-app test is on a recording or listed in the allow-list (${external.listed} listed)`]
  ];
  for (const [n, text, items, okText] of rules) {
    lines.push(`  ${n ? '!!' : 'OK'}  determinism: ${n ? text : okText}`);
    if (n) warn.push(`determinism: ${text}\n    ${shown(items, (x) => x, 5)}`);
  }

  console.log(lines.join('\n'));
  const failing = strict ? [...fail, ...warn] : fail;
  if (failing.length) {
    console.error('\nCoverage register check failed:\n  ' + failing.join('\n  '));
    process.exit(1);
  }
  if (warn.length) console.log('  (only an unmapped inventory item or a missing proof fails CI; the rest fails under --strict)');
}

if (process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1])) await main();
