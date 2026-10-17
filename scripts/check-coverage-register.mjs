// The coverage register (docs/testing/coverage-register.json) must account for everything Deluno does, and every proof it names must exist.
//
//   node scripts/check-coverage-register.mjs                run the checks; (a), (b), (e) and (f) fail, (c) and the determinism counts are reported
//   node scripts/check-coverage-register.mjs --strict       also fail on any gap (a row whose proofs do not reach its required level) and any rule breach
//   node scripts/check-coverage-register.mjs --stubs        print a register row stub for every inventory item without a row
//   node scripts/check-coverage-register.mjs --base origin/main   also fail when there are more gaps than in the register at that git ref (the ratchet)
//   node scripts/check-coverage-register.mjs --report [file]      write the statistics as markdown (default artifacts/coverage-register.md, not committed)
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
//     inproc and is counted in the totals.
//     container-smoke: a step of a workflow in .github/workflows (named by its `- name:`), or a container script in scripts/ (named "*"), that
//     really starts the container: its text must run `docker run` or `docker compose up`. A step that only builds, configures or scans an image
//     is refused (counts as inproc). Real for a disk or install row that is not "vm", the same as real-fs.
//     A row marked "vm": true can only be proven by a real install (NTFS-specific behaviour, network shares,
//     cross-volume moves and links, the installer, the update, the tray, Windows service mode): it needs a scenario proof, nothing else reaches real.
// (d) with --base: the number of gaps is above the register's count at that git ref. The gaps only go down, measured against the branch a
//     change goes into, so no number is kept in the file for parallel changes to fight over. A rise needs a `gapIncrease` entry in the
//     register, { "from": <gaps on the base>, "to": <gaps now>, "reason": "..." }, matching that rise exactly.
// (e) a row that is not a gap but still carries `issue` is stale; (f) a gap with no `issue` is unowned. A gap is a row below its required level,
//     or marked `"incomplete": true` (its proofs do not yet cover its whole claim; `partial` stays the list of part-proofs that never count).
// The statistics (totals by severity, area and proof level) are not kept in the repository: they go to $GITHUB_STEP_SUMMARY when that is
// set, and to the --report file.
// Only (a), (b), (d), (e) and (f) fail the default run. A malformed register, a stale cover, a proof of the wrong type and the rules below are reported.
// Determinism rules: a fixed sleep in a test without a `reason:` comment on its line fails the run (#1273); under --strict the rules below also fail:
// every golden-path scenario runnable alone; every external-app test on a recording or in tests/recorded-reply-allow-list.json.
import { spawnSync } from 'node:child_process';
import { appendFileSync, existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { CONTAINER_SMOKE_FILE, TestIndex, read, walk, slash } from './coverage/testindex.mjs';
import { buildInventory, countsOf, scenarioPlan } from './coverage/inventory.mjs';

export const PROOF_TYPES = ['vitest', 'smoke', 'backend', 'scenario', 'recording-replay', 'real-fs', 'container-smoke'];
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

/** Does a container-smoke proof count for this row? Same rule as real-fs: a disk or install row that is not "vm", and the named step must really start the container. */
export function containerSmokeCounts(proof, row, index) {
  if (proof.type !== 'container-smoke' || !row || row.vm === true || !REAL_FS_TOUCHES.has(row.touches)) return false;
  return !!index && typeof index.containerSmoke === 'function' && index.containerSmoke(proof.file, proof.name).real;
}
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
  if (proof.type === 'container-smoke') return containerSmokeCounts(proof, row, index) ? 2 : 1;
  if (REAL_TYPES.has(proof.type)) return 2;
  if (proof.type === 'smoke' && row && SCREEN_AREAS.has(row.area) && !smokeState(proof, index).stubbed) return 2;
  return 1;
}
export const rowLevel = (row, index) => (row.proofs ?? []).reduce((best, p) => Math.max(best, proofLevel(p, row, index)), 0);
/** A row is a gap when its best proof is below its required level, or when it is marked `"incomplete": true` (its proofs do not yet cover its whole claim). */
export const isGap = (row, index) => row.incomplete === true || rowLevel(row, index) < requiredLevel(row);

/** (e) a row that is not a gap but still names an issue is stale; (f) a gap with no issue has no owner. Rows are listed by id. */
export const staleIssueRows = (register, index) => register.rows.filter((row) => row.issue != null && !isGap(row, index)).map((row) => row.id);
export const unownedGapRows = (register, index) => register.rows.filter((row) => row.issue == null && isGap(row, index)).map((row) => row.id);

/** A `gapIncrease` entry says which rise it explains and why: { "from": <gaps on the base>, "to": <gaps now>, "reason": "..." }. */
export function gapIncreaseShape(entry) {
  if (entry === undefined) return [];
  const whole = (n) => Number.isInteger(n) && n >= 0;
  if (!entry || !whole(entry.from) || !whole(entry.to) || entry.to <= entry.from || typeof entry.reason !== 'string' || !entry.reason.trim()) {
    return ['"gapIncrease" must be { "from": <gaps on the base branch>, "to": <gaps now, more than from>, "reason": "<why the gaps go up>" }'];
  }
  return [];
}

/** The register's own shape: ids, enums, and the rule deriving the required level from severity and what the row touches. */
export function validateShape(register) {
  const problems = [];
  const areas = new Set(Object.keys(register.areas ?? {}));
  const ids = new Set();
  if ('gapCeiling' in register) problems.push('"gapCeiling" is not used: the gaps are held to the count on the base branch (--base), so remove it from the register');
  problems.push(...gapIncreaseShape(register.gapIncrease));
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
    if (row.incomplete != null && row.incomplete !== true) problems.push(`${where}: incomplete may only be true (leave it out otherwise)`);
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
    case 'container-smoke': {
      if (!CONTAINER_SMOKE_FILE.test(p.file)) return `a container-smoke proof must be a packaging workflow or a container script in scripts, not ${p.file}`;
      const r = index.containerSmoke(p.file, p.name);
      return r.real ? null : `container-smoke refused (counts as inproc): ${r.why}`;
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

export const gapCountOf = (register, index) => register.rows.filter((row) => isGap(row, index)).length;

/**
 * (d) the ratchet: the gaps may only go down from the count on the base branch. A rise is allowed only with a `gapIncrease` entry whose
 * "from" is that count, whose "to" is the count now and whose reason says why. With no base to compare with (no --base, or a base without a
 * register) nothing is held back, and a note says so.
 */
export function gapRatchet(register, gapCount, baseGaps) {
  if (baseGaps === null || baseGaps === undefined) return { problems: [], notes: ['No register on the base branch to compare with, so the gaps were not held to a count.'] };
  if (gapCount <= baseGaps) return { problems: [], notes: [] };
  const entry = register.gapIncrease;
  const reason = typeof entry?.reason === 'string' ? entry.reason.trim() : '';
  if (entry && entry.from === baseGaps && entry.to === gapCount && reason) {
    return { problems: [], notes: [`The gaps rise from ${baseGaps} to ${gapCount}: ${reason}`] };
  }
  const mismatch = entry && (entry.from !== baseGaps || entry.to !== gapCount) ? ` The "gapIncrease" entry says ${entry.from} to ${entry.to}, which is not this rise.` : '';
  return {
    problems: [
      `there are ${gapCount} gaps, more than the ${baseGaps} in the register on the base branch: a gap cannot be added; give the new row a proof that reaches its required level. ` +
        `A rise that cannot be avoided is written under "gapIncrease" in docs/testing/coverage-register.json, in a pull request of its own: { "from": ${baseGaps}, "to": ${gapCount}, "reason": "..." }.${mismatch}`
    ],
    notes: []
  };
}

/** The gaps in the register at a git ref, judged by the tests in this checkout; null when there is no register there to read. */
export function baseGapCount(root, ref, index) {
  const run = spawnSync('git', ['show', `${ref}:docs/testing/coverage-register.json`], { cwd: root, encoding: 'utf8', maxBuffer: 256 * 1024 * 1024 });
  if (run.status !== 0) return null;
  try {
    const base = JSON.parse(run.stdout);
    return Array.isArray(base.rows) ? gapCountOf(base, index) : null;
  } catch {
    return null;
  }
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
  const container = { claims: 0, counted: 0, refused: 0, noEffect: 0 };
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
    for (const pr of row.proofs ?? []) if (pr.type === 'container-smoke') {
      container.claims++;
      if (!(index && typeof index.containerSmoke === 'function' && index.containerSmoke(pr.file, pr.name).real)) container.refused++;
      else if (containerSmokeCounts(pr, row, index)) container.counted++;
      else container.noEffect++;
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
  return { all, byArea, bySeverity, byLevel, highByTouches, smoke, realFs, container, gaps, highGaps: gaps.filter((g) => g.severity === 'high').length };
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

export function evaluate({ register, inventory, index, plan, root, baseGaps = null }) {
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
  const t = safe('totals', () => totals(register, index), { all: { rows: 0, proven: 0, gaps: 0 }, byArea: {}, bySeverity: { high: { rows: 0, proven: 0, gaps: 0 }, medium: { rows: 0, proven: 0, gaps: 0 }, low: { rows: 0, proven: 0, gaps: 0 } }, byLevel: { none: 0, inproc: 0, real: 0 }, highByTouches: {}, smoke: { proofs: 0, unstubbed: 0, stubbed: 0, undecided: 0 }, realFs: { claims: 0, counted: 0, refused: 0, noEffect: 0, vmRows: 0, vmRowGaps: 0 }, container: { claims: 0, counted: 0, refused: 0, noEffect: 0 }, gaps: [], highGaps: 0 });
  const sleeps = safe('sleep rule', () => sleepViolations(index), []);
  const scenarios = safe('scenario rule', () => (plan.length ? scenarioViolations(plan, index) : []), []);
  const ratchet = safe('gap ratchet', () => gapRatchet(register, gapCountOf(register, index), baseGaps), { problems: [], notes: [] });
  const staleIssues = safe('stale issue rule', () => staleIssueRows(register, index), []);
  const unownedGaps = safe('unowned gap rule', () => unownedGapRows(register, index), []);
  return { shape, unmapped, stale, missing, typeProblems, totals: t, sleeps, scenarios, internal, staleIssues, unownedGaps, gapProblems: ratchet.problems, gapNotes: ratchet.notes };
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

export const REPORT_FILE = 'artifacts/coverage-register.md';

/** The statistics as markdown, for the CI job summary and the --report file. They are not kept in the repository. */
export function statisticsReport(register, inventory, t) {
  const pct = (a, b) => (b ? `${Math.round((a / b) * 100)}%` : '-');
  const lines = [];
  lines.push('### Coverage register', '', 'Generated by `node scripts/check-coverage-register.mjs` from `coverage-register.json`; the rules are in docs/testing/COVERAGE.md.', '');
  lines.push(`**${t.all.rows}** behaviours. **${t.all.proven}** reach their required proof level (${pct(t.all.proven, t.all.rows)}); **${t.all.gaps}** are gaps, **${t.highGaps}** of them high.`, '');
  lines.push('### By severity', '', '| Severity | Rows | Reach their level | Gaps |', '| --- | ---: | ---: | ---: |');
  for (const s of SEVERITIES) lines.push(`| ${s} | ${t.bySeverity[s].rows} | ${t.bySeverity[s].proven} | ${t.bySeverity[s].gaps} |`);
  lines.push('', '### High rows by what they touch (these need `real`, except `none`, which needs `inproc`)', '', '| Touches | High rows | High gaps |', '| --- | ---: | ---: |');
  for (const k of TOUCHES) lines.push(`| ${k} | ${t.highByTouches[k]?.rows ?? 0} | ${t.highByTouches[k]?.gaps ?? 0} |`);
  lines.push('', '### By best proof a row has', '', '| Best proof | Rows |', '| --- | ---: |');
  for (const l of LEVEL_NAMES) lines.push(`| ${l === 'none' ? 'none (a gap)' : l} | ${t.byLevel[l]} |`);
  lines.push('', `Smoke proofs: **${t.smoke.proofs}**; unstubbed (can reach \`real\` for a screen behaviour) **${t.smoke.unstubbed}**; stubbed **${t.smoke.stubbed}**; not decidable statically, counted as stubbed **${t.smoke.undecided}**.`);
  lines.push('', `Real-fs proofs: **${t.realFs.claims}** claimed; **${t.realFs.counted}** verified and counted as real; **${t.realFs.refused}** refused (the checker could not verify a real temp folder or database file with no fake, so they count as inproc); **${t.realFs.noEffect}** verified but without effect (the row is "vm", or does not touch disk or install). **${t.realFs.vmRows}** rows are "vm" (only a real install can prove them, so they need a scenario); **${t.realFs.vmRowGaps}** of them are still gaps.`);
  lines.push('', `Container-smoke proofs: **${t.container.claims}** claimed; **${t.container.counted}** verified and counted as real; **${t.container.refused}** refused (the named step or script does not start the container image, so they count as inproc); **${t.container.noEffect}** verified but without effect (the row is "vm", or does not touch disk or install).`);
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

/** What the CI job summary shows: the statistics, then what the ratchet noted (a reasoned rise, or no base to compare with). */
export function jobSummary(register, inventory, t, notes) {
  const lines = [statisticsReport(register, inventory, t)];
  for (const note of notes) lines.push('', `- ${note}`);
  return `${lines.join('\n')}\n`;
}

export function writeReport(root, file, register, inventory, t) {
  const target = resolve(root, file);
  mkdirSync(dirname(target), { recursive: true });
  writeFileSync(target, `${statisticsReport(register, inventory, t)}\n`);
  return target;
}

const stubFor = (item) => ({
  id: `${item.kind}.${item.key.split(':')[1].replace(/[^A-Za-z0-9]+/g, '-').toLowerCase()}`,
  area: '<area>',
  behaviour: `<one plain sentence about ${item.label ?? item.key}>`,
  kind: { route: 'API route', webroute: 'screen action', uisurface: 'screen action', uiaction: 'screen action', service: 'job', lane: 'job', handler: 'job', task: 'job', healthcheck: 'job', migration: 'migration', integration: 'integration-operation', tray: 'tray', scenario: 'integration-operation' }[item.kind],
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
  const valueOf = (flag) => { const i = args.indexOf(flag); return i >= 0 ? args[i + 1] : undefined; };
  const baseRef = valueOf('--base');
  let index, inventory, planResult, plan, result, baseGaps = null;
  try {
    index = new TestIndex(root);
    if (baseRef) baseGaps = baseGapCount(root, baseRef, index);
    planResult = scenarioPlan(root);
    plan = planResult.ids;
    inventory = buildInventory(root, { skipScenarios: plan.length === 0 });
    result = evaluate({ register, inventory, index, plan, root, baseGaps });
  } catch (error) {
    // The check itself broke. Only an unmapped item or a missing proof may fail CI, so say so and stop.
    console.error(`Coverage register check could not run: ${error?.stack ?? error}`);
    process.exit(strict ? 1 : 0);
  }
  const external = await externalAppViolations(root);
  const t = result.totals;

  const reportValue = valueOf('--report');
  if (args.includes('--report')) console.log(`wrote ${writeReport(root, reportValue && !reportValue.startsWith('--') ? reportValue : REPORT_FILE, register, inventory, t)}`);

  if (args.includes('--stubs')) {
    console.log(JSON.stringify(result.unmapped.map(stubFor), null, 2));
    return;
  }
  if (args.includes('--json')) {
    console.log(JSON.stringify({ rows: t.all, bySeverity: t.bySeverity, highByTouches: t.highByTouches, smoke: t.smoke, realFs: t.realFs, container: t.container, byLevel: t.byLevel, byArea: t.byArea, highGaps: t.highGaps, inventory: countsOf(inventory) }, null, 1));
    return;
  }
  if (args.includes('--list-gaps')) {
    const order = { high: 0, medium: 1, low: 2 };
    for (const g of [...t.gaps].sort((a, b) => order[a.severity] - order[b.severity] || a.id.localeCompare(b.id))) console.log(`${g.severity.padEnd(6)} ${g.touches.padEnd(9)} ${g.id}  ${g.behaviour.slice(0, 100)}`);
    return;
  }

  const lines = [];
  const fail = []; // the only things that fail CI: (a), (b), (d), (e) and (f)
  const warn = []; // everything else is reported, and fails only under --strict
  const shown = (list, f, n = 10) => list.slice(0, n).map(f).join('\n    ') + (list.length > n ? `\n    ... and ${list.length - n} more` : '');
  lines.push(`Coverage register: ${register.rows.length} rows, ${inventory.length} inventory items (${Object.entries(countsOf(inventory)).map(([k, n]) => `${n} ${k}`).join(', ')}).`);
  if (planResult.error) lines.push(`  !! ${planResult.error}: scenario ids are not checked.`);

  if (result.unmapped.length) fail.push(`(a) ${result.unmapped.length} inventory item(s) have no register row. Add a row with the key in "covers" (node scripts/check-coverage-register.mjs --stubs prints stubs):\n    ${shown(result.unmapped, (i) => `${i.key}  (${i.file ?? ''})`)}`);
  else lines.push('  OK  (a) every inventory item has a register row');
  if (result.missing.length) fail.push(`(b) ${result.missing.length} proof(s) name a file or test that does not exist (fix or remove the proof; the row is then a gap):\n    ${shown(result.missing, (m) => `${m.row}: ${m.why}`)}`);
  else lines.push('  OK  (b) every proof names a file and a test that exist');

  if (result.gapProblems.length) fail.push(`(d) the gaps:\n    ${shown(result.gapProblems, (x) => x)}`);
  else if (baseRef && baseGaps !== null) lines.push(`  OK  (d) ${t.gaps.length} gaps, no more than the ${baseGaps} at ${baseRef}`);
  if (result.staleIssues.length) fail.push(`(e) ${result.staleIssues.length} row(s) are not gaps but still name an issue (remove their "issue"):
    ${shown(result.staleIssues, (x) => x)}`);
  else lines.push('  OK  (e) no row names an issue once it is no gap');
  if (result.unownedGaps.length) fail.push(`(f) ${result.unownedGaps.length} gap(s) name no issue (set "issue" to the area's coverage issue):
    ${shown(result.unownedGaps, (x) => x)}`);
  else lines.push('  OK  (f) every gap names the issue that will close it');
  if (!baseRef) lines.push('  !!  (d) no --base given, so the gaps were not held to the count on the base branch (CI passes it; locally use --base origin/main)');
  else if (baseGaps === null) lines.push(`  !!  (d) no register at ${baseRef} to compare with, so the gaps were not held to a count`);
  for (const note of result.gapNotes) if (baseGaps !== null) lines.push(`  !!  ${note}`);

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
  lines.push(`      container-smoke proofs ${t.container.claims}: ${t.container.counted} counted, ${t.container.refused} refused (counted inproc), ${t.container.noEffect} without effect.`);
  if (t.gaps.length) warn.push(levelLine);

  const failing_rules = new Set();
  const rules = [
    [result.sleeps.length, `${result.sleeps.length} fixed sleep(s) in tests without a "reason:" comment on the line`, result.sleeps.map((s) => `${s.file}:${s.line}`), 'no fixed sleep in a test without a reason'],
    [result.scenarios.length, `${result.scenarios.length} golden-path scenario(s) not runnable alone`, result.scenarios.map((s) => `${s.id}: ${s.why}`), `every golden-path scenario is runnable alone (${plan.length} ids)`],
    [external.problems.length, `${external.problems.length} external-app test problem(s): a reply built inline and not in tests/recorded-reply-allow-list.json (${external.listed} files listed there)`, external.problems, `every external-app test is on a recording or listed in the allow-list (${external.listed} listed)`]
  ];
  failing_rules.add(rules[0][2]); // a fixed sleep without a reason fails the default run
  for (const [n, text, items, okText] of rules) {
    lines.push(`  ${n ? '!!' : 'OK'}  determinism: ${n ? text : okText}`);
    if (n) (failing_rules.has(items) ? fail : warn).push(`determinism: ${text}\n    ${shown(items, (x) => x, 5)}`);
  }

  if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, jobSummary(register, inventory, t, result.gapNotes));
  console.log(lines.join('\n'));
  const failing = strict ? [...fail, ...warn] : fail;
  if (failing.length) {
    console.error('\nCoverage register check failed:\n  ' + failing.join('\n  '));
    process.exit(1);
  }
  if (warn.length) console.log('  (only an unmapped inventory item, a missing proof or a rise in the gaps fails CI; the rest fails under --strict)');
}

if (process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1])) await main();
