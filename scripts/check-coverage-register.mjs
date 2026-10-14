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
// (c) every row's proofs reach its required level: a high row needs a "real" proof (a golden-VM scenario, or a test that replays the
//     other app's recorded real reply), every other row at least an "inproc" one. Reported as a total today; --strict fails on it.
// Determinism rules, reported with counts and failing under --strict: no fixed sleep in a test without a `reason:` comment on its line;
// every golden-path scenario runnable alone; every external-app test on a recording or in tests/recorded-reply-allow-list.json.
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { TestIndex, read, walk, slash } from './coverage/testindex.mjs';
import { buildInventory, countsOf, scenarioPlan } from './coverage/inventory.mjs';

export const PROOF_TYPES = ['vitest', 'smoke', 'backend', 'scenario', 'recording-replay'];
export const KINDS = ['screen action', 'state', 'API route', 'job', 'integration-operation', 'failure-mode', 'migration', 'install-update', 'tray'];
export const SEVERITIES = ['high', 'medium', 'low'];
export const REAL_TYPES = new Set(['scenario', 'recording-replay']);
const LEVEL_NAMES = ['none', 'inproc', 'real'];

export const proofLevel = (proof) => (REAL_TYPES.has(proof.type) ? 2 : 1);
export const rowLevel = (row) => (row.proofs ?? []).reduce((best, p) => Math.max(best, proofLevel(p)), 0);
export const requiredLevel = (row) => (row.severity === 'high' ? 2 : 1);
export const isGap = (row) => rowLevel(row) < requiredLevel(row);

/** The register's own shape: ids, enums, and the hard rule that a high row needs real proof. */
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
    if (row.severity === 'high' && row.required !== 'real') problems.push(`${where}: a high row requires "real" proof`);
    if (row.severity !== 'high' && !['inproc', 'real'].includes(row.required)) problems.push(`${where}: required must be "inproc" or "real"`);
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
        if (!index.has(p.file)) { bad.push({ row: row.id, label, proof: p, why: `the file ${p.file} does not exist` }); continue; }
        const typeProblem = typeMismatch(p, index);
        if (typeProblem) { bad.push({ row: row.id, label, proof: p, why: typeProblem }); continue; }
        if (p.name === '*') continue;
        if (p.type === 'scenario' && plan && plan.has(p.name)) continue;
        if (!index.hasName(p.file, p.name)) bad.push({ row: row.id, label, proof: p, why: `no test, group or class "${p.name}" in ${p.file}` });
      }
    }
  }
  return bad;
}

/** (c): totals. */
export function totals(register) {
  const byArea = {};
  const bySeverity = { high: blank(), medium: blank(), low: blank() };
  const byLevel = { none: 0, inproc: 0, real: 0 };
  const all = blank();
  const gaps = [];
  function blank() { return { rows: 0, proven: 0, gaps: 0 }; }
  for (const row of register.rows) {
    const gap = isGap(row);
    const a = (byArea[row.area] ??= { ...blank(), high: 0, highGaps: 0, level: { none: 0, inproc: 0, real: 0 } });
    for (const t of [a, bySeverity[row.severity], all]) { t.rows++; if (gap) t.gaps++; else t.proven++; }
    a.high += row.severity === 'high' ? 1 : 0;
    if (gap && row.severity === 'high') a.highGaps++;
    const lv = LEVEL_NAMES[rowLevel(row)];
    a.level[lv]++;
    byLevel[lv]++;
    if (gap) gaps.push(row);
  }
  return { all, byArea, bySeverity, byLevel, gaps, highGaps: gaps.filter((g) => g.severity === 'high').length };
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
  const shape = validateShape(register);
  // Without PowerShell the golden-path plan cannot be read: its scenario rows are then neither required nor judged stale.
  const { unmapped, stale } = mapInventory(register, inventory, { checkStale: true, ignoreStale: plan.length ? [] : ['scenario:'] });
  const planIds = plan.length ? plan.map((p) => p.id) : null;
  const missing = missingProofs(register, index, planIds);
  const t = totals(register);
  const sleeps = sleepViolations(index);
  const scenarios = plan.length ? scenarioViolations(plan, index) : [];
  return { shape, unmapped, stale, missing, totals: t, sleeps, scenarios };
}

function externalAppViolations(root) {
  const file = join(root, 'scripts/check-recorded-replies.mjs');
  if (!existsSync(file)) return Promise.resolve({ problems: [], offenders: new Map(), listed: 0 });
  return import(pathToFileURL(file).href).then((m) => {
    const { problems, offenders } = m.check(root);
    return { problems, offenders, listed: offenders.size };
  });
}

const START = '<!-- coverage:generated:start -->';
const END = '<!-- coverage:generated:end -->';

export function generatedSection(register, inventory, t) {
  const pct = (a, b) => (b ? `${Math.round((a / b) * 100)}%` : '-');
  const lines = [];
  lines.push(`Generated by \`node scripts/check-coverage-register.mjs --write-doc\` from \`coverage-register.json\`. Do not edit by hand.`, '');
  lines.push(`**${t.all.rows}** behaviours. **${t.all.proven}** reach their required proof level (${pct(t.all.proven, t.all.rows)}); **${t.all.gaps}** are gaps, **${t.highGaps}** of them high.`, '');
  lines.push('### By severity', '', '| Severity | Required proof | Rows | Reach it | Gaps |', '| --- | --- | ---: | ---: | ---: |');
  for (const s of SEVERITIES) lines.push(`| ${s} | ${s === 'high' ? 'real' : 'inproc'} | ${t.bySeverity[s].rows} | ${t.bySeverity[s].proven} | ${t.bySeverity[s].gaps} |`);
  lines.push('', '### By best proof a row has', '', '| Best proof | Rows |', '| --- | ---: |');
  for (const l of LEVEL_NAMES) lines.push(`| ${l === 'none' ? 'none (a gap)' : l} | ${t.byLevel[l]} |`);
  lines.push('', '### By area', '', '| Area | What | Rows | Reach it | Gaps | High rows | High gaps | No proof | Inproc | Real |', '| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |');
  for (const [area, a] of Object.entries(t.byArea).sort()) {
    lines.push(`| ${area} | ${register.areas[area]?.name ?? ''} | ${a.rows} | ${a.proven} | ${a.gaps} | ${a.high} | ${a.highGaps} | ${a.level.none} | ${a.level.inproc} | ${a.level.real} |`);
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
  if (!existsSync(registerPath)) {
    console.error('docs/testing/coverage-register.json is missing.');
    process.exit(1);
  }
  const register = JSON.parse(read(registerPath));
  const index = new TestIndex(root);
  const planResult = scenarioPlan(root);
  const plan = planResult.ids;
  const inventory = buildInventory(root, { skipScenarios: plan.length === 0 });
  const result = evaluate({ register, inventory, index, plan, root });
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
    console.log(JSON.stringify({ rows: t.all, bySeverity: t.bySeverity, byLevel: t.byLevel, byArea: t.byArea, highGaps: t.highGaps, inventory: countsOf(inventory) }, null, 1));
    return;
  }
  if (args.includes('--list-gaps')) {
    const order = { high: 0, medium: 1, low: 2 };
    for (const g of [...t.gaps].sort((a, b) => order[a.severity] - order[b.severity] || a.id.localeCompare(b.id))) console.log(`${g.severity.padEnd(6)} ${g.id}  ${g.behaviour.slice(0, 110)}`);
    return;
  }

  const lines = [];
  const fail = [];
  const shown = (list, f, n = 10) => list.slice(0, n).map(f).join('\n    ') + (list.length > n ? `\n    ... and ${list.length - n} more` : '');
  lines.push(`Coverage register: ${register.rows.length} rows, ${inventory.length} inventory items (${Object.entries(countsOf(inventory)).map(([k, n]) => `${n} ${k}`).join(', ')}).`);
  if (planResult.error) lines.push(`  !! ${planResult.error}: scenario ids are not checked.`);

  if (result.shape.length) { fail.push(`the register is malformed (${result.shape.length}):\n    ${shown(result.shape, (x) => x)}`); }
  if (result.unmapped.length) fail.push(`(a) ${result.unmapped.length} inventory item(s) have no register row. Add a row with the key in "covers" (node scripts/check-coverage-register.mjs --stubs prints stubs):\n    ${shown(result.unmapped, (i) => `${i.key}  (${i.file ?? ''})`)}`);
  else lines.push('  OK  (a) every inventory item has a register row');
  if (result.stale.length) fail.push(`(a) ${result.stale.length} row(s) cover an inventory item that no longer exists (remove the row or the key):\n    ${shown(result.stale, (s) => `${s.row} -> ${s.key}`)}`);
  if (result.missing.length) fail.push(`(b) ${result.missing.length} proof(s) name a file or test that does not exist, or claim a type they do not have (fix the proof; the row is a gap until it has a real one):\n    ${shown(result.missing, (m) => `${m.row}: ${m.why}`)}`);
  else lines.push('  OK  (b) every proof names a test that exists, of the type it claims');

  const levelLine = `(c) ${t.gaps.length} gaps (${t.highGaps} high): ${t.all.proven} of ${t.all.rows} rows reach their required proof level (high needs real; the rest inproc).`;
  lines.push(`  ${t.gaps.length ? '!!' : 'OK'}  ${levelLine}`);
  lines.push(`      by severity: ${SEVERITIES.map((s) => `${s} ${t.bySeverity[s].gaps}/${t.bySeverity[s].rows}`).join(', ')} gaps; best proof: ${LEVEL_NAMES.map((l) => `${l} ${t.byLevel[l]}`).join(', ')}.`);
  if (strict && t.gaps.length) fail.push(levelLine + ' (--strict)');

  const rules = [
    [result.sleeps.length, `${result.sleeps.length} fixed sleep(s) in tests without a "reason:" comment on the line`, result.sleeps.map((s) => `${s.file}:${s.line}`), 'no fixed sleep in a test without a reason'],
    [result.scenarios.length, `${result.scenarios.length} golden-path scenario(s) not runnable alone`, result.scenarios.map((s) => `${s.id}: ${s.why}`), `every golden-path scenario is runnable alone (${plan.length} ids)`],
    [external.problems.length, `${external.problems.length} external-app test problem(s): a reply built inline and not in tests/recorded-reply-allow-list.json (${external.listed} files listed there)`, external.problems, `every external-app test is on a recording or listed in the allow-list (${external.listed} listed)`]
  ];
  for (const [n, text, items, okText] of rules) {
    lines.push(`  ${n ? '!!' : 'OK'}  determinism: ${n ? text : okText}`);
    if (strict && n) fail.push(`determinism: ${text}\n    ${shown(items, (x) => x, 5)}`);
  }

  console.log(lines.join('\n'));
  if (fail.length) {
    console.error('\nCoverage register check failed:\n  ' + fail.join('\n  '));
    process.exit(1);
  }
  if (!strict && (t.gaps.length || rules.some(([n]) => n))) console.log('  (gaps and rule breaches do not fail yet; node scripts/check-coverage-register.mjs --strict fails on them)');
}

if (process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1])) await main();
